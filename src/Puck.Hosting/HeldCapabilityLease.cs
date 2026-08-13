namespace Puck.Hosting;

/// <summary>The backing store for a held capability: it yields the capability while the grant is live and,
/// when the grant was revocable, serves as the grantor's <see cref="ICapabilityTakeBack"/>. A delegated lease resolves
/// through its parent, so local or ancestor revocation makes the holder's
/// <see cref="IHostContext.HoldsCapability{TCapability}"/> stop resolving it. A permanent origin lease has no parent and
/// is created irrevocable.</summary>
internal sealed class HeldCapabilityLease : ICapabilityTakeBack {
    private readonly object? m_capability;
    private readonly HeldCapabilityLease? m_parent;
    private readonly bool m_revocable;
    private int m_revoked;

    public HeldCapabilityLease(object capability, bool revocable) {
        ArgumentNullException.ThrowIfNull(capability);

        m_capability = capability;
        m_revocable = revocable;
    }

    public HeldCapabilityLease(HeldCapabilityLease parent, bool revocable) {
        ArgumentNullException.ThrowIfNull(parent);

        m_parent = parent;
        m_revocable = revocable;
    }

    /// <inheritdoc />
    public bool IsRevoked => (Resolve() is null);

    /// <summary>Returns the granted capability while the grant is live, or <see langword="null"/> once revoked.</summary>
    public object? Resolve() {
        if (0 != Volatile.Read(location: ref m_revoked)) {
            return null;
        }

        return (m_parent?.Resolve() ?? m_capability);
    }
    /// <inheritdoc />
    public void Revoke() {
        if (!m_revocable) {
            throw new InvalidOperationException(message: "This capability was granted irrevocably (\"no take backsies\") and cannot be reclaimed.");
        }

        _ = Interlocked.Exchange(location1: ref m_revoked, value: 1);
    }
}
