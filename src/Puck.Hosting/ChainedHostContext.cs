namespace Puck.Hosting;

/// <summary>
/// An <see cref="IHostContext"/> that resolves from a primary context first and falls back to a secondary
/// one. This is the seam a host uses to publish its own capabilities over those it inherited from its
/// parent: an inherited capability published at any ancestor (e.g. the device context) propagates down the
/// tree, and a host may shadow it for a child subtree (e.g. handing a DirectX child a DirectX device). Held
/// capabilities, by contrast, resolve from the primary only — they never flow through the inherited fallback.
/// </summary>
public sealed class ChainedHostContext : IHostContext {
    private readonly IHostContext m_fallback;
    private readonly IHostContext m_primary;

    public ChainedHostContext(IHostContext primary, IHostContext fallback) {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);

        m_fallback = fallback;
        m_primary = primary;
    }

    /// <inheritdoc />
    public bool TryResolveCapability<TCapability>(out TCapability capability) where TCapability : class {
        return (
            m_primary.TryResolveCapability(capability: out capability) ||
            m_fallback.TryResolveCapability(capability: out capability)
        );
    }
    /// <inheritdoc />
    /// <remarks>A held capability is the host's own grant only — never the inherited fallback — so it does
    /// not propagate to a child unless this host explicitly re-grants it on the child's primary context.</remarks>
    public bool HoldsCapability<TCapability>(out TCapability capability) where TCapability : class {
        return m_primary.HoldsCapability(capability: out capability);
    }
}
