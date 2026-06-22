namespace Puck.Input.Devices;

/// <summary>
/// The optional input features a controller actually provides. Mirrors the output
/// <see cref="Puck.Input.Output.GamepadOutputCapabilities"/> so a consumer can branch on real hardware support
/// (gyro aiming, analog-trigger ramps) instead of silently receiving neutral data from a family that lacks the
/// sensor — Xbox pads report no gyro, the Switch Pro's shoulder triggers are digital, and so on.
/// </summary>
[Flags]
public enum GamepadInputCapabilities {
    /// <summary>No optional input features beyond the buttons and sticks every family provides.</summary>
    None = 0,
    /// <summary>A motion sensor reporting angular velocity (gyro), in radians/second.</summary>
    Gyro = 1 << 0,
    /// <summary>Pressure-sensitive (analog) triggers rather than digital shoulder buttons.</summary>
    AnalogTriggers = 1 << 1,
}
