namespace Puck.Abstractions.Memory;

/// <summary>
/// The claims on storage several holders read, of which the last one frees it: a holder takes a claim before it keeps the
/// storage past the call that handed it over and releases it once its last read has ended, and the release that takes
/// the count to zero is the one place the storage is freed. A count that has reached zero takes no claim again, so a holder
/// that names the storage without a claim of its own (a list named ahead, a reader on another thread) asks with
/// <see cref="TryAcquire"/> and learns the storage is gone rather than reviving it. Lock-free: every step is one
/// interlocked operation on the count, so any thread may claim and release.
/// <para>The count is a mutable value: keep it in a field that is not <see langword="readonly"/> and call through that
/// field, never through a copy.</para>
/// </summary>
/// <param name="claims">The claims the storage starts with, held by whoever made it; at least one.</param>
public struct ClaimCount(int claims) {
    private int m_claims = ((claims > 0) ? claims : throw new ArgumentOutOfRangeException(paramName: nameof(claims), actualValue: claims, message: "Storage starts with the claim of whoever made it."));

    /// <summary>Gets whether the last claim was released, so nothing may claim or read the storage again.</summary>
    public readonly bool Released => (Volatile.Read(location: in m_claims) == 0);

    /// <summary>Takes a claim unless the last one was already released.</summary>
    /// <returns><see langword="true"/> when the claim was taken; <see langword="false"/> when the storage is gone.</returns>
    public bool TryAcquire() {
        var claims = Volatile.Read(location: ref m_claims);

        while (claims > 0) {
            var seen = Interlocked.CompareExchange(location1: ref m_claims, value: (claims + 1), comparand: claims);

            if (seen == claims) { return true; }
            claims = seen;
        }

        return false;
    }
    /// <summary>Takes a claim the caller knows the storage can still take: it holds one itself, or a holder it reads under
    /// does.</summary>
    /// <param name="owner">What the storage belongs to, named by the refusal.</param>
    /// <exception cref="ObjectDisposedException">The last claim was already released.</exception>
    public void Acquire(object owner) {
        if (!TryAcquire()) { throw new ObjectDisposedException(objectName: owner.GetType().Name, message: "Storage was claimed after its last claim was released."); }
    }
    /// <summary>Releases one claim.</summary>
    /// <returns><see langword="true"/> when this was the last claim: the caller frees the storage, once.</returns>
    /// <exception cref="InvalidOperationException">More claims were released than taken.</exception>
    public bool Release() {
        var claims = Interlocked.Decrement(location: ref m_claims);

        if (claims < 0) {
            _ = Interlocked.Increment(location: ref m_claims);
            throw new InvalidOperationException(message: "A claim was released twice.");
        }

        return (claims == 0);
    }
}
