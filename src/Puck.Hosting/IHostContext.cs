namespace Puck.Hosting;

/// <summary>
/// The capability seam a host hands down to its children, with two propagation policies. <em>Inherited</em>
/// capabilities (e.g. a device context) flow to every descendant and are resolved with
/// <see cref="TryResolveCapability{TCapability}"/>. <em>Held</em> capabilities (e.g. the terminal-control
/// baton, or input focus) are granted to a single holder, do not propagate to children, and are checked
/// with <see cref="HoldsCapability{TCapability}"/> — the basis of the capability-permission system. The
/// contract exists at every level of the recursive tree.
/// </summary>
public interface IHostContext {
    /// <summary>Resolves an <em>inherited</em> capability the host (or an ancestor) published; returns
    /// <see langword="false"/> when none is available. Inherited capabilities flow to every descendant. (A
    /// held capability is not found here — check it with <see cref="HoldsCapability{TCapability}"/>.)</summary>
    bool TryResolveCapability<TCapability>(out TCapability capability) where TCapability : class;

    /// <summary>Returns whether this context <em>holds</em> the given capability — one granted to a single
    /// holder and, unlike an inherited capability, not propagated to children: a host withholds it by default
    /// and re-grants it explicitly. Yields the capability when held; returns <see langword="false"/> when not.</summary>
    bool HoldsCapability<TCapability>(out TCapability capability) where TCapability : class;
}
