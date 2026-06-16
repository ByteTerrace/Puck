namespace Puck.Hosting;

/// <summary>The backing store for a held capability: it yields the capability while the grant is live and,
/// when the grant was revocable, serves as the grantor's <see cref="ICapabilityTakeBack"/>. Revoking nulls
/// the capability so the holder's <see cref="IHostContext.HoldsCapability{TCapability}"/> stops resolving it.
/// A permanent lease (the origin holder, never passed on) is created irrevocable and simply never revoked.</summary>
internal sealed class HeldCapabilityLease : ICapabilityTakeBack {
    private readonly bool m_revocable;
    private volatile object? m_capability;

    public HeldCapabilityLease(object capability, bool revocable) {
        ArgumentNullException.ThrowIfNull(capability);

        m_capability = capability;
        m_revocable = revocable;
    }

    /// <inheritdoc />
    public bool IsRevoked => (m_capability is null);

    /// <summary>Returns the granted capability while the grant is live, or <see langword="null"/> once revoked.</summary>
    public object? Resolve() {
        return m_capability;
    }
    /// <inheritdoc />
    public void Revoke() {
        if (!m_revocable) {
            throw new InvalidOperationException(message: "This capability was granted irrevocably (\"no take backsies\") and cannot be reclaimed.");
        }

        m_capability = null;
    }
}
