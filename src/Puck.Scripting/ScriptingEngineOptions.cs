namespace Puck.Scripting;

/// <summary>
/// The pinned, deterministic configuration values a <see cref="ScriptingEngine"/> builds its
/// <c>Wasmtime.Config</c> from. These are explicit engine choices, never library defaults, so the
/// fuel-halt point and stack ceiling are a fixed part of the replay contract.
/// </summary>
/// <param name="DefaultFuelPerTick">The per-tick fuel budget an addon without its own override runs under.</param>
/// <param name="MaxStackBytes">The guest execution stack ceiling in bytes.</param>
/// <param name="MaxCommandRecords">The maximum number of 24-byte command records the host accepts per tick.</param>
public readonly record struct ScriptingEngineOptions(long DefaultFuelPerTick, int MaxStackBytes, int MaxCommandRecords) {
    /// <summary>Gets the locked deterministic preset drawn from <see cref="AddonAbi"/>'s frozen budgets.</summary>
    public static ScriptingEngineOptions Deterministic => new(
        DefaultFuelPerTick: AddonAbi.DefaultFuelPerTick,
        MaxCommandRecords: AddonAbi.MaxCommandRecords,
        MaxStackBytes: AddonAbi.MaxStackBytes
    );
}
