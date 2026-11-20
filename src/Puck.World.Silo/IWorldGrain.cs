namespace Puck.World.Silo;

/// <summary>Orleans' own activation-lifecycle door onto one hosted row — the grain's key (owner oid + world id
/// extension) already names which row, so no member here repeats it. Every silo verb that touches a row's
/// lifecycle calls the grain (<see cref="Orleans.IGrainFactory"/>), never the simulation host directly, so
/// activation has exactly one path whether Orleans, cloud edge, or console asks.</summary>
public interface IWorldGrain : IGrainWithGuidCompoundKey {
    /// <summary>Activates this row, allowing up to three minutes for document composition, checkpoint recovery and
    /// the baseline checkpoint that covers the recovered state.</summary>
    /// <returns><see langword="true"/> once the row is ready to receive submissions and a landed checkpoint covers
    /// everything it restored.</returns>
    [ResponseTimeout("00:03:00")]
    [Replaces(root: typeof(object))]
    Task<bool> ActivateAsync();
    /// <summary>Requests an immediate checkpoint of this row, allowing up to three minutes for the upload, which
    /// queues behind the row's earlier publications.</summary>
    /// <returns><see langword="true"/> when the checkpoint captured and wrote successfully.</returns>
    [ResponseTimeout("00:03:00")]
    Task<bool> CheckpointNowAsync();
    /// <summary>Deactivates this row, capturing and uploading a final checkpoint first, within three minutes.</summary>
    [ResponseTimeout("00:03:00")]
    Task DeactivateAsync();
    /// <summary>Reads this row's own current status.</summary>
    Task<WorldGrainStatus?> StatusAsync();
}
