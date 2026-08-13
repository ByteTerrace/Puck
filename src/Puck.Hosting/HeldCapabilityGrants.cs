namespace Puck.Hosting;

/// <summary>
/// The set of held capabilities a host grants into a child's context when it passes the baton (or input
/// focus, or any held capability) on. Each grant is <em>revocable by default</em>: <see cref="Grant"/>
/// returns the grantor's <see cref="ICapabilityTakeBack"/> handle, which force-reclaims it. Passing
/// <c>revocable: false</c> — "no take backsies" — returns <see langword="null"/>: the immediate grantor cannot take
/// that grant back, although revoking an ancestor grant still invalidates the complete delegation chain.
/// Hand the populated set to <see cref="HostContext"/> to build the grantee's context.
/// </summary>
public sealed class HeldCapabilityGrants {
    private readonly Dictionary<Type, HeldCapabilityLease> m_leases = new();

    /// <summary>Delegates a capability currently held by another host context into this set. The new lease retains the
    /// source lease as its parent, so revoking any ancestor also revokes this grant.</summary>
    /// <typeparam name="TCapability">The held capability's contract type.</typeparam>
    /// <param name="grantor">The context that currently holds the capability.</param>
    /// <param name="revocable">Whether the grantor may force-reclaim it later. <see langword="true"/> by
    /// default; pass <see langword="false"/> for an irrevocable ("no take backsies") grant.</param>
    /// <returns>The grantor's take-back handle when <paramref name="revocable"/> is <see langword="true"/>;
    /// otherwise <see langword="null"/>, since an irrevocable grant cannot be reclaimed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="grantor"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="grantor"/> does not expose a live held capability of
    /// type <typeparamref name="TCapability"/>.</exception>
    public ICapabilityTakeBack? Grant<TCapability>(IHostContext grantor, bool revocable = true) where TCapability : class {
        ArgumentNullException.ThrowIfNull(grantor);

        if (
            (grantor is not IHeldCapabilityLeaseSource source) ||
            !source.TryResolveHeldLease(
                capabilityType: typeof(TCapability),
                lease: out var parent
            )
        ) {
            throw new InvalidOperationException(
                message: $"The grantor does not hold a live {typeof(TCapability).FullName} capability that can be delegated."
            );
        }

        var lease = new HeldCapabilityLease(
            parent: parent,
            revocable: revocable
        );

        m_leases[typeof(TCapability)] = lease;

        return (revocable
            ? lease
            : null);
    }

    internal IReadOnlyDictionary<Type, HeldCapabilityLease> Leases => m_leases;
}
