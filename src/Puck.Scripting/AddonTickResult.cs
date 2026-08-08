namespace Puck.Scripting;

/// <summary>The outcome of a single addon tick. Deliberately carries no span — decoded cells live in the
/// instance's reusable buffer, read synchronously via <see cref="AddonInstance.OutCells"/> immediately after
/// the tick.</summary>
/// <param name="Status">Whether the tick ran and decoded cleanly.</param>
/// <param name="CellCount">The number of structurally-decoded output cells available this tick (<c>0</c> when faulted).</param>
/// <param name="FuelConsumed">The fuel consumed this tick (<c>budget - remaining</c>).</param>
/// <param name="Fault">The fault detail when <see cref="Status"/> is <see cref="AddonTickStatus.Faulted"/>.</param>
public readonly record struct AddonTickResult(AddonTickStatus Status, int CellCount, ulong FuelConsumed, AddonFault Fault) {
    /// <summary>Creates a successful result.</summary>
    /// <param name="cellCount">The number of structurally-decoded output cells.</param>
    /// <param name="fuelConsumed">The fuel consumed this tick.</param>
    /// <returns>An <see cref="AddonTickStatus.Ok"/> result.</returns>
    public static AddonTickResult Ok(int cellCount, ulong fuelConsumed) {
        return new AddonTickResult(
            CellCount: cellCount,
            Fault: AddonFault.None,
            FuelConsumed: fuelConsumed,
            Status: AddonTickStatus.Ok
        );
    }

    /// <summary>Creates a faulted result.</summary>
    /// <param name="fault">The sticky fault detail.</param>
    /// <param name="fuelConsumed">The fuel consumed before the fault, if any.</param>
    /// <returns>An <see cref="AddonTickStatus.Faulted"/> result.</returns>
    public static AddonTickResult Faulted(AddonFault fault, ulong fuelConsumed = 0) {
        return new AddonTickResult(
            CellCount: 0,
            Fault: fault,
            FuelConsumed: fuelConsumed,
            Status: AddonTickStatus.Faulted
        );
    }
}
