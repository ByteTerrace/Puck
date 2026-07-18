namespace Puck.Input.Devices;

/// <summary>Identifies a recognized controller family, used to pick a parser and per-type binding overrides.</summary>
public enum GamepadType {
    /// <summary>An unrecognized device, or one not yet identified.</summary>
    Unknown = 0,
    /// <summary>A Microsoft Xbox One controller.</summary>
    XboxOne = 1,
    /// <summary>A Microsoft Xbox Series X|S controller.</summary>
    XboxSeries = 2,
    /// <summary>A Sony PlayStation 5 DualSense controller.</summary>
    PlayStation5 = 3,
    /// <summary>A Nintendo Switch Pro controller.</summary>
    SwitchPro = 4,
    /// <summary>A Valve Steam Controller (dual-trackpad controller with a single analog stick and grip paddles).</summary>
    SteamController = 5,
    /// <summary>A Valve Steam Controller (2026, codename Triton): dual sticks + dual trackpads, four rear paddles, and a 6-axis IMU, over its wireless puck receiver.</summary>
    SteamControllerTriton = 6,
}
