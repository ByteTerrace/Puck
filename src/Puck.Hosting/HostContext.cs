namespace Puck.Hosting;

/// <summary>Default <see cref="IHostContext"/>: resolves inherited capabilities from a type-keyed table and
/// held capabilities from a set of leases a host builds for its children. Held capabilities may be permanent
/// (the origin holder) or granted revocably via <see cref="HeldCapabilityGrants"/> (a passed baton the
/// grantor can force-reclaim). <see cref="Empty"/> publishes nothing.</summary>
public sealed class HostContext : IHostContext {
    private static readonly IReadOnlyDictionary<Type, object> None = new Dictionary<Type, object>();
    private static readonly IReadOnlyDictionary<Type, HeldCapabilityLease> NoneHeld = new Dictionary<Type, HeldCapabilityLease>();
    private readonly IReadOnlyDictionary<Type, object> m_capabilities;
    private readonly IReadOnlyDictionary<Type, HeldCapabilityLease> m_heldLeases;

    /// <summary>A context that publishes no capabilities and holds nothing.</summary>
    public static HostContext Empty { get; } = new HostContext(capabilities: None);

    /// <summary>Initializes a context with inherited capabilities and optional <em>permanent</em> held
    /// capabilities — held capabilities the holder never passes on and so cannot be reclaimed.</summary>
    /// <param name="capabilities">The inherited capabilities, keyed by type, that flow to every descendant.</param>
    /// <param name="heldCapabilities">The permanent held capabilities, keyed by type, granted to this holder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is <see langword="null"/>.</exception>
    public HostContext(IReadOnlyDictionary<Type, object> capabilities, IReadOnlyDictionary<Type, object>? heldCapabilities = null) {
        ArgumentNullException.ThrowIfNull(capabilities);

        m_capabilities = capabilities;
        m_heldLeases = ((heldCapabilities is null)
            ? NoneHeld
            : WrapPermanent(heldCapabilities: heldCapabilities));
    }

    /// <summary>Initializes a context with inherited capabilities and <em>granted</em> held capabilities —
    /// passed on with <see cref="HeldCapabilityGrants"/>, each revocable unless granted "no take backsies".</summary>
    /// <param name="capabilities">The inherited capabilities, keyed by type, that flow to every descendant.</param>
    /// <param name="heldGrants">The held capabilities granted to this holder.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public HostContext(IReadOnlyDictionary<Type, object> capabilities, HeldCapabilityGrants heldGrants) {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(heldGrants);

        m_capabilities = capabilities;
        m_heldLeases = heldGrants.Leases;
    }

    /// <inheritdoc />
    public bool TryResolveCapability<TCapability>(out TCapability capability) where TCapability : class {
        if (
            m_capabilities.TryGetValue(
                key: typeof(TCapability),
                value: out var resolved
            ) &&
            (resolved is TCapability typed)
        ) {
            capability = typed;

            return true;
        }

        capability = null!;

        return false;
    }
    /// <inheritdoc />
    public bool HoldsCapability<TCapability>(out TCapability capability) where TCapability : class {
        if (
            m_heldLeases.TryGetValue(
                key: typeof(TCapability),
                value: out var lease
            ) &&
            (lease.Resolve() is TCapability typed)
        ) {
            capability = typed;

            return true;
        }

        capability = null!;

        return false;
    }

    private static IReadOnlyDictionary<Type, HeldCapabilityLease> WrapPermanent(IReadOnlyDictionary<Type, object> heldCapabilities) {
        var leases = new Dictionary<Type, HeldCapabilityLease>(capacity: heldCapabilities.Count);

        foreach (var (type, capability) in heldCapabilities) {
            leases[type] = new HeldCapabilityLease(
                capability: capability,
                revocable: false
            );
        }

        return leases;
    }
}
