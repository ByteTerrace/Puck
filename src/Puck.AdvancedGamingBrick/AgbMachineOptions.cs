namespace Puck.AdvancedGamingBrick;

/// <summary>Immutable per-machine diagnostic overrides. Defaults model the cartridge and bus normally;
/// options travel with a fork and never depend on process-wide configuration.</summary>
public sealed record AgbMachineOptions {
    /// <summary>Disables game-pak prefetch even when software enables it, for timing diagnostics.</summary>
    public bool DisablePrefetch { get; init; }
    /// <summary>Suppresses a detected cartridge RTC, for device-isolation diagnostics.</summary>
    public bool DisableRtc { get; init; }
    /// <summary>Receives formatted bus-access traces synchronously on the calling thread, or null for no trace.
    /// A fork inherits the callback; the caller must arrange concurrency if it runs siblings on different threads.
    /// Trace output is observational and is excluded from snapshots and machine identity.</summary>
    public Action<string>? BusTrace { get; init; }
}
