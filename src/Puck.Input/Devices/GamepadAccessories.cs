namespace Puck.Input.Devices;

/// <summary>The accessories plugged into a controller, as its reports describe them.</summary>
[Flags]
public enum GamepadAccessories : byte {
    /// <summary>Nothing is plugged in, or the family reports no accessories.</summary>
    None = 0,
    /// <summary>Headphones (or a headset's earpieces) are plugged in.</summary>
    Headphones = (1 << 0),
    /// <summary>A microphone (or a headset's microphone) is plugged in.</summary>
    Microphone = (1 << 1),
}
