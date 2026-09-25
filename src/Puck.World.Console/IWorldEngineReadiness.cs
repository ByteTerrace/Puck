namespace Puck.World;

/// <summary>
/// Whether the world's rendering engine is ready: its pipeline set is installed and it has produced its first frame. It
/// is the one readiness fact the console waits on (<c>world.wait ready</c>) and a scheduled capture's hold reads
/// (<see cref="WorldCaptureScheduler"/>). A host that composes no renderer registers none. Members are read on the host
/// pump.
/// </summary>
public interface IWorldEngineReadiness {
    /// <summary>Gets whether the engine is ready. Reading it allocates nothing, so a hold polls it every drain.</summary>
    bool IsReady { get; }
    /// <summary>Gets why the engine is not ready, naming its pipeline build and how far it has come, or
    /// <see langword="null"/> once it is ready. It builds a string, so a caller reads it only to report.</summary>
    string? NotReadyReason { get; }
}
