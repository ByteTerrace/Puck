namespace Puck.World.Silo;

/// <summary>Orleans' own activation-lifecycle door onto one hosted row — the grain's key (owner oid + world id
/// extension) already names which row, so no member here repeats it. Every silo verb that touches a row's
/// lifecycle calls the grain (<see cref="Orleans.IGrainFactory"/>), never <see cref="WorldSiloHost"/> directly, so
/// activation has exactly one path whether Orleans or the console asks.</summary>
internal interface IWorldGrain : IGrainWithGuidCompoundKey {
    /// <summary>Activates this row.</summary>
    /// <returns><see langword="true"/> once the row is ready to receive submissions.</returns>
    Task<bool> ActivateAsync();
    /// <summary>Requests an immediate checkpoint of this row.</summary>
    /// <returns><see langword="true"/> when the checkpoint captured and wrote successfully.</returns>
    Task<bool> CheckpointNowAsync();
    /// <summary>Deactivates this row, capturing a final checkpoint first.</summary>
    Task DeactivateAsync();
    /// <summary>Reads this row's own current status.</summary>
    Task<WorldGrainStatus?> StatusAsync();
}
