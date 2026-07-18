namespace Puck.Abstractions.Lighting;

/// <summary>
/// Classifies the kind of device an <see cref="ILampArrayDevice"/> is, as the HID LampArray
/// <c>LampArrayKind</c> attribute reports it. A consumer picks its effect by kind — a keyboard color-codes keys,
/// a mouse tints a scroll wheel — without matching on vendor or product ids.
/// </summary>
public enum LampArrayKind {
    /// <summary>The kind was not reported or is not one of the recognized values.</summary>
    Undefined = 0,
    /// <summary>A keyboard (the per-key surface a bind legend paints).</summary>
    Keyboard = 1,
    /// <summary>A mouse.</summary>
    Mouse = 2,
    /// <summary>A game controller.</summary>
    GameController = 3,
    /// <summary>A peripheral (headset stand, dock, or other accessory) not covered by a more specific kind.</summary>
    Peripheral = 4,
    /// <summary>A scene / ambient light (a strip or bulb that lights an environment).</summary>
    Scene = 5,
    /// <summary>A notification light.</summary>
    Notification = 6,
    /// <summary>A chassis (case) light.</summary>
    Chassis = 7,
    /// <summary>A wearable light.</summary>
    Wearable = 8,
    /// <summary>A piece of furniture with integrated lighting.</summary>
    Furniture = 9,
    /// <summary>An art piece with integrated lighting.</summary>
    Art = 10,
    /// <summary>A headset.</summary>
    Headset = 11,
}
