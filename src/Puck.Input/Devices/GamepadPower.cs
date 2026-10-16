namespace Puck.Input.Devices;

/// <summary>Where a controller draws its power from, as its own reports describe it.</summary>
public enum GamepadPowerState : byte {
    /// <summary>The controller has not reported its power state, or its family reports none.</summary>
    Unknown = 0,
    /// <summary>Running on its battery, unplugged.</summary>
    OnBattery = 1,
    /// <summary>Powered by its cable and carrying no battery (a wired-only pad).</summary>
    NoBattery = 2,
    /// <summary>Plugged in and charging its battery.</summary>
    Charging = 3,
    /// <summary>Plugged in with its battery full.</summary>
    Charged = 4,
    /// <summary>Plugged in but not charging, because the charger reports a fault or a temperature or voltage limit.</summary>
    NotCharging = 5,
}
/// <summary>A controller's power report: its <see cref="GamepadPowerState"/> and battery charge.</summary>
/// <param name="State">Where the controller draws power from.</param>
/// <param name="Percent">The battery charge, 0..100, at the resolution the family reports (a DualSense steps by
/// 10, a Switch Pro by 25, XInput by about 33); meaningless when <paramref name="State"/> is
/// <see cref="GamepadPowerState.Unknown"/> or <see cref="GamepadPowerState.NoBattery"/>.</param>
public readonly record struct GamepadPower(GamepadPowerState State, byte Percent) {
    /// <summary>Gets the report of a controller whose power state is not known.</summary>
    public static GamepadPower Unknown => default;
}
