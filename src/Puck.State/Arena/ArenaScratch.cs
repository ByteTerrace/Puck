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
