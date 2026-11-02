using System.Runtime.CompilerServices;
using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.State;

/// <summary>Working storage an evaluation borrows and returns: the buffers a board transform, a sort, a vector
/// ranking, or an expression's value stack would otherwise size from the document on the machine stack. A lease is
/// as wide as the document asks, so a ceiling bounds work and memory rather than stack depth.</summary>
/// <remarks>
/// <para>Leases are returned in the reverse of the order they were taken, which a <see langword="using"/>
/// declaration guarantees. A lease taken while another of the same element type is open gets its own buffer, so a
/// nested evaluation never writes over its caller's.</para>
/// <para>A buffer is kept for the next lease at the same depth and grows by doubling, so an evaluation that has run
/// once at its widest allocates nothing afterwards. The storage is no part of simulation state: nothing hashes,
/// exports, or checkpoints it. It is not thread-safe; it serves the one evaluation its owner has in flight.</para>
/// </remarks>
public sealed class ArenaScratch {
    /// <summary>Gets or sets the destination key while a state write evaluates its value.</summary>
    public CellKey WriteKey { get; set; }

    /// <summary>Gets the first fault raised inside the innermost open fault scope, or
    /// <see cref="ExpressionFault.None"/>.</summary>
    public ExpressionFault RaisedFault => m_raisedFault;

    private ExpressionFault m_raisedFault;
    private int m_faultScopes;

    /// <summary>Raises a fault into the innermost open fault scope; the scope keeps the first one it was raised.
    /// <see cref="ExpressionFault.None"/> raises nothing.</summary>
    /// <param name="fault">The fault.</param>
    /// <exception cref="InvalidOperationException">No fault scope is open: the evaluation that met the fault has no
    /// caller deciding what it means, an engine defect rather than a value to drop.</exception>
    public void RaiseFault(ExpressionFault fault) {
        if (fault == ExpressionFault.None) {
            return;
        }
        if (m_faultScopes == 0) {
            throw new InvalidOperationException(message: $"an evaluation raised a {fault} fault outside any fault scope; every evaluation runs inside the scope of the caller that decides what its fault means");
        }
        if (m_raisedFault == ExpressionFault.None) {
            m_raisedFault = fault;
        }
    }
    /// <summary>Opens a fault scope: faults raised until it is left are its own, read through
    /// <see cref="RaisedFault"/> while it is open and returned by <see cref="LeaveFaultScope"/>. Scopes nest, and
    /// leaving one passes its fault on to the scope around it, so no enclosing decision misses a fault an inner one
    /// already answered.</summary>
    /// <returns>The scope, to hand back to <see cref="LeaveFaultScope"/>.</returns>
    public FaultScope EnterFaultScope() {
        var scope = new FaultScope(Outer: m_raisedFault);

        m_raisedFault = ExpressionFault.None;
        m_faultScopes++;

        return scope;
    }
    /// <summary>Closes the innermost fault scope.</summary>
    /// <param name="scope">The scope <see cref="EnterFaultScope"/> returned.</param>
    /// <returns>The first fault raised inside the scope, or <see cref="ExpressionFault.None"/>.</returns>
    /// <exception cref="InvalidOperationException">No fault scope is open.</exception>
    public ExpressionFault LeaveFaultScope(FaultScope scope) {
        if (m_faultScopes == 0) {
            throw new InvalidOperationException(message: "no fault scope is open");
        }

        var raised = m_raisedFault;

        m_faultScopes--;
        m_raisedFault = ((m_faultScopes == 0)
            ? ExpressionFault.None
            : ((scope.Outer != ExpressionFault.None) ? scope.Outer : raised)
        );

        return raised;
    }

    /// <summary>An open fault scope: the fault the enclosing scope had raised when this one opened.</summary>
    /// <param name="Outer">The enclosing scope's fault.</param>
    public readonly record struct FaultScope(ExpressionFault Outer);

    private static int SlotCount;

    private object?[] m_pools = new object?[8];

    private static class Slot<T> {
        public static readonly int Index = (Interlocked.Increment(location: ref SlotCount) - 1);
    }
    private sealed class Pool<T> {
        public T[]?[] Buffers = new T[]?[4];
        public int Depth;
    }

    /// <summary>One borrowed buffer. Disposing it returns the buffer.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    public ref struct Lease<T> {
        private readonly Pool<T> m_pool;

        internal Lease(object pool, Span<T> span) {
            m_pool = Unsafe.As<Pool<T>>(o: pool);
            Span = span;
        }

        /// <summary>Gets the borrowed elements, every one default-valued when the lease was taken.</summary>
        public Span<T> Span { get; }

        /// <summary>Returns the buffer.</summary>
        public readonly void Dispose() {
            // A buffer that held references must not keep what they pointed at alive until the next lease.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
                Span.Clear();
            }

            m_pool.Depth--;
        }
    }

    private WorkCount m_leased;

    // The running count of elements every lease has borrowed, which the owning arena reports as
    // ArenaWork.ScratchLeasedElements.
    internal long LeasedElements => m_leased.Value;

    /// <summary>Borrows <paramref name="length"/> default-valued elements.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="length">The element count; zero or less borrows an empty span.</param>
    /// <returns>The lease.</returns>
    public Lease<T> Rent<T>(int length) {
        var index = Slot<T>.Index;

        if (index >= m_pools.Length) {
            Array.Resize(
                array: ref m_pools,
                newSize: Math.Max(
                    val1: (index + 1),
                    val2: (m_pools.Length * 2)
                )
            );
        }

        var pool = Unsafe.As<Pool<T>>(o: (m_pools[index] ??= new Pool<T>()));

        if (pool.Depth == pool.Buffers.Length) {
            Array.Resize(
                array: ref pool.Buffers,
                newSize: (pool.Depth * 2)
            );
        }

        length = Math.Max(
            val1: 0,
            val2: length
        );

        var buffer = pool.Buffers[pool.Depth];

        if ((buffer is null) || (buffer.Length < length)) {
            buffer = new T[Math.Max(
                val1: 16,
                val2: ((int)Math.Min(
                    val1: ((long)Array.MaxLength),
                    val2: ((long)((uint)length).NextPowerOfTwo())
                ))
            )];
            pool.Buffers[pool.Depth] = buffer;
        }

        pool.Depth++;
        m_leased.Add(amount: length);

        var span = buffer.AsSpan(
            length: length,
            start: 0
        );

        // A buffer that held references was cleared when it was returned.
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            span.Clear();
        }

        return new Lease<T>(
            pool: pool,
            span: span
        );
    }
}
