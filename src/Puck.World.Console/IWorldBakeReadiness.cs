namespace Puck.World;

/// <summary>
/// Whether the presentation's mesh-path bakes are settled: every residency it renders (the world's own, each session
/// view's, each routed scene's and each infinity layer's) has baked every group of the program it last drew, so no
/// lattice is owed and no contour still runs. It is the fact the console waits on (<c>world.wait bakes</c>), so a
/// script that reads the baked frame waits on the bake rather than on a tick count. A host that composes no renderer
/// registers none. Members are read on the host pump.
/// </summary>
public interface IWorldBakeReadiness {
    /// <summary>Gets whether the bakes are settled. Reading it allocates nothing, so a hold polls it every drain.</summary>
    bool IsSettled { get; }
    /// <summary>Gets which residencies still bake, by name, or <see langword="null"/> once the bakes are settled. It
    /// builds a string, so a caller reads it only to report.</summary>
    string? PendingReason { get; }
}
