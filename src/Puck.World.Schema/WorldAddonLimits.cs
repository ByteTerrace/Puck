namespace Puck.World;

/// <summary>Document admission ceilings that bound addon mounting and work on every simulation tick.</summary>
public static class WorldAddonLimits {
    /// <summary>Allows large addon collections without admitting unbounded native stores per document.</summary>
    public const int Addons = 64;
    /// <summary>Ten times the normal guest budget; prevents one declared budget from monopolizing a tick.</summary>
    public const ulong FuelPerAddon = 10_000_000;
    /// <summary>Bounds total guest execution even when a document declares many individually valid budgets.</summary>
    public const ulong FuelPerDocument = 100_000_000;
    /// <summary>The default charged and executed for a zero document budget, matching the normal ABI budget.</summary>
    public const ulong DefaultFuel = 1_000_000;
    /// <summary>Bounds manifest resolution and retained capability handles across all document addons.</summary>
    public const int RequestsPerDocument = 4096;
    /// <summary>Bounds machine-memory polling and retained watch state across all document addons.</summary>
    public const int MemoryWatchesPerDocument = 4096;
}
