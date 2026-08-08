namespace Puck.World.Protocol;

/// <summary>
/// The closed addon-lifecycle union a <see cref="WorldSubmissionPayload.AddonLifecycle"/> submission carries —
/// <c>world.addon.mount</c>/<c>world.addon.unmount</c> travel as this leaf through the ONE ordered domain, so a
/// live mount lands at the same defined tick-boundary point a document mutation does and rides the replay tape
/// through <see cref="WorldSubmissionCodec"/>'s shared leaf codec (see <c>Server.WorldAddonRuntime.Mount</c>/
/// <c>.Unmount</c>). Deliberately narrower than <see cref="WorldDefinition.Addons"/>' <c>world.row.set addons</c>/
/// <c>world.row.remove addons</c> mutations, which are DOCUMENT-ONLY (what the NEXT boot mounts) and never reach the
/// live runtime: this union is the RUNTIME-facing counterpart — materializing (or tearing down) a guest in the
/// CURRENT session, live, ordered, tape-covered.
/// </summary>
public abstract record WorldAddonLifecycle {
    private WorldAddonLifecycle() {
    }

    /// <summary>Live-mounts a NEW guest. Refuses (by name, at apply) a name already tracked in the mounted set —
    /// mount never re-admits an existing guest; <c>world.addon.reload</c> is the refresh verb for that case.</summary>
    /// <param name="Name">The addon's identifying name — must be unique among mounted guests.</param>
    /// <param name="ModulePath">The WASM module file path (machine-local, resolved exactly as a boot row's is).</param>
    /// <param name="Hash">The REQUIRED content-address integrity pin (<c>sha256-64/{16 hex}</c>).</param>
    /// <param name="Fuel">The per-tick fuel budget; <c>0</c> selects the guest ABI's own default.</param>
    /// <param name="Requests">The addon's manifest — what it asks for, as data; null/empty means it asks for
    /// nothing and therefore reaches nothing (deny-by-default holds regardless of what a later grant names).</param>
    public sealed record Mount(string Name, string ModulePath, string Hash, ulong Fuel, IReadOnlyList<WorldCapabilityRequest>? Requests) : WorldAddonLifecycle;

    /// <summary>Fully unmounts a guest by name — stronger than a disable: the guest leaves the mounted set and its
    /// receipt entirely rather than staying tracked-but-skipped.</summary>
    /// <param name="Name">The addon name.</param>
    public sealed record Unmount(string Name) : WorldAddonLifecycle;
}
