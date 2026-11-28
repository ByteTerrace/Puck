using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// A buffer its owner lends to holders that may keep it past the owner's own use: a render graph that borrows a
/// package's output publishes it to readers whose submissions and bindings outlive the owner's tables. The owner holds
/// one claim, which <see cref="Dispose"/> returns; each <see cref="Lend"/> takes another, which its loan returns; the
/// buffer is disposed by whichever return is the last (<see cref="ClaimCount"/>), on that caller's thread.
/// <para>Invariant: <see cref="BufferHandle"/> names a live buffer while any claim stands. A holder that keeps the
/// buffer past the call that handed it over takes a loan; a lent buffer handed on without one is safe only for as long
/// as a claim the giver holds stands.</para>
/// </summary>
public sealed class GpuLentBuffer : IGpuBuffer {
    private readonly IGpuBuffer m_buffer;

    private ClaimCount m_claims = new(claims: 1);
    private int m_ownerReturned;

    /// <summary>Initializes a new instance of the <see cref="GpuLentBuffer"/> class, taking ownership of a buffer under
    /// the caller's claim.</summary>
    /// <param name="buffer">The buffer; ownership moves to the new instance, which disposes it once the last claim is
    /// returned.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    public GpuLentBuffer(IGpuBuffer buffer) {
        ArgumentNullException.ThrowIfNull(argument: buffer);
        m_buffer = buffer;
    }

    /// <inheritdoc/>
    public nint BufferHandle => m_buffer.BufferHandle;
    /// <inheritdoc/>
    public ulong SizeBytes => m_buffer.SizeBytes;
    /// <summary>Gets whether the last claim was returned and the buffer disposed.</summary>
    public bool Released => m_claims.Released;

    /// <summary>Takes a claim on the buffer for a holder that keeps it.</summary>
    /// <returns>The loan, which returns the claim when disposed; disposing it again returns nothing.</returns>
    /// <exception cref="ObjectDisposedException">The last claim was already returned.</exception>
    public IDisposable Lend() {
        m_claims.Acquire(owner: this);
        return new Loan(buffer: this);
    }
    /// <summary>Returns the owner's claim; the buffer is disposed now when no loan stands, else by the last loan's
    /// return. A second call returns nothing.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(location1: ref m_ownerReturned, value: 1) == 0) { Return(); }
    }

    private void Return() {
        if (m_claims.Release()) { m_buffer.Dispose(); }
    }

    private sealed class Loan(GpuLentBuffer buffer) : IDisposable {
        private int m_returned;

        public void Dispose() {
            if (Interlocked.Exchange(location1: ref m_returned, value: 1) == 0) { buffer.Return(); }
        }
    }
}
