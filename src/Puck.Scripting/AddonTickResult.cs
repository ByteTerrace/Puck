namespace Puck.Scripting;

/// <summary>The outcome of a single addon tick. Deliberately carries no span — decoded records live in the
/// instance's reusable buffer, read synchronously via <see cref="AddonInstance.Commands"/> immediately after
/// the tick.</summary>
/// <param name="Status">Whether the tick ran and decoded cleanly.</param>
/// <param name="CommandCount">The number of decoded command records available this tick (<c>0</c> when faulted).</param>
/// <param name="FuelConsumed">The fuel consumed this tick (<c>budget - remaining</c>).</param>
/// <param name="Fault">The fault detail when <see cref="Status"/> is <see cref="AddonTickStatus.Faulted"/>.</param>
public readonly record struct AddonTickResult(AddonTickStatus Status, int CommandCount, ulong FuelConsumed, AddonFault Fault) {
    /// <summary>Creates a successful result.</summary>
    /// <param name="commandCount">The number of decoded command records.</param>
    /// <param name="fuelConsumed">The fuel consumed this tick.</param>
    /// <returns>An <see cref="AddonTickStatus.Ok"/> result.</returns>
    public static AddonTickResult Ok(int commandCount, ulong fuelConsumed) {
        return new AddonTickResult(
            CommandCount: commandCount,
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
            CommandCount: 0,
            Fault: fault,
            FuelConsumed: fuelConsumed,
            Status: AddonTickStatus.Faulted
        );
    }
}
