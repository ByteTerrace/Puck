namespace Puck.World;

/// <summary>
/// Whether the presentation's creation bakes are settled: every prototype of the definition it last saw is baked, held
/// or refused, and none is queued or baking. It is the fact the console waits on (<c>world.wait bakes</c>), so a script
/// that reads a drawn bake waits on the bake rather than on a tick count. A host that bakes nothing registers none.
/// Members are read on the host pump.
/// </summary>
public interface IWorldBakeReadiness {
    /// <summary>Gets whether the bakes are settled. Reading it allocates nothing, so a hold polls it every drain.</summary>
    bool IsSettled { get; }
}
