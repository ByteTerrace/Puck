namespace Puck.World.Server;

/// <summary>The opaque prepare/commit transaction handle crossing the <see cref="IWorldAddonHost"/> seam — this
/// project carries it between <see cref="IWorldAddonHost.TryPrepare"/> and <see cref="IWorldAddonHost.Commit"/>
/// without knowing its concrete shape (<c>Addons.PreparedAddonInstall</c> is the one implementation), so this
/// project never depends on the concrete addon runtime. Every non-commit exit — a downstream gate refuses the
/// mutation, an undo probe was only ever meant to be discarded, shutdown — must dispose an outstanding plan
/// synchronously; a plan carries the freshly-instantiated guest stores that never reached
/// <see cref="IWorldAddonHost.Commit"/> and nothing else will release them.</summary>
public interface IWorldAddonPreparedPlan : IDisposable {
    /// <summary>Gets the mounted-guest count this plan installs once committed — the sizing bound
    /// <see cref="WorldServer"/> pre-allocates its per-tick addon contention tracking against BEFORE calling
    /// <see cref="IWorldAddonHost.Commit"/>, so the new arrays are ready to adopt by reference the instant the plan
    /// itself publishes, with no allocation at the moment of the swap.</summary>
    int MountedCount { get; }
}
