namespace Puck.Hosting;

/// <summary>
/// The set of held capabilities a host grants into a child's context when it passes the baton (or input
/// focus, or any held capability) on. Each grant is <em>revocable by default</em>: <see cref="Grant"/>
/// returns the grantor's <see cref="ICapabilityTakeBack"/> handle, which force-reclaims it. Passing
/// <c>revocable: false</c> — "no take backsies" — returns <see langword="null"/>: the grant is then
/// permanent and can never be taken back, so the grantee keeps it until it yields or passes it on itself.
/// Hand the populated set to <see cref="HostContext"/> to build the grantee's context.
/// </summary>
public sealed class HeldCapabilityGrants {
    private readonly Dictionary<Type, HeldCapabilityLease> m_leases = new();

    /// <summary>Grants a held capability into this set.</summary>
    /// <param name="capability">The capability to grant.</param>
    /// <param name="revocable">Whether the grantor may force-reclaim it later. <see langword="true"/> by
    /// default; pass <see langword="false"/> for an irrevocable ("no take backsies") grant.</param>
    /// <returns>The grantor's take-back handle when <paramref name="revocable"/> is <see langword="true"/>;
    /// otherwise <see langword="null"/>, since an irrevocable grant cannot be reclaimed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capability"/> is <see langword="null"/>.</exception>
    public ICapabilityTakeBack? Grant<TCapability>(TCapability capability, bool revocable = true) where TCapability : class {
        ArgumentNullException.ThrowIfNull(capability);

        var lease = new HeldCapabilityLease(
            capability: capability,
            revocable: revocable
        );

        m_leases[typeof(TCapability)] = lease;

        return (revocable
            ? lease
            : null);
    }

    internal IReadOnlyDictionary<Type, HeldCapabilityLease> Leases => m_leases;
}
