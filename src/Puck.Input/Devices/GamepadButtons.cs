namespace Puck.Input.Devices;

/// <summary>
/// The digital buttons of a normalized controller, as a bit set. Face buttons use a platform-neutral
/// South/East/West/North vocabulary so bindings stay family-agnostic: South is the bottom face button (Xbox A,
/// PlayStation cross, Switch B-position), and so on clockwise.
/// </summary>
[Flags]
public enum GamepadButtons : uint {
    /// <summary>No buttons pressed.</summary>
    None = 0u,
    /// <summary>The bottom face button (Xbox A / PlayStation Cross / Switch B).</summary>
    ButtonSouth = (1u << 0),
    /// <summary>The right face button (Xbox B / PlayStation Circle / Switch A).</summary>
    ButtonEast = (1u << 1),
    /// <summary>The left face button (Xbox X / PlayStation Square / Switch Y).</summary>
    ButtonWest = (1u << 2),
    /// <summary>The top face button (Xbox Y / PlayStation Triangle / Switch X).</summary>
    ButtonNorth = (1u << 3),
    /// <summary>The up direction of the directional pad.</summary>
    DpadUp = (1u << 4),
    /// <summary>The down direction of the directional pad.</summary>
    DpadDown = (1u << 5),
    /// <summary>The left direction of the directional pad.</summary>
    DpadLeft = (1u << 6),
    /// <summary>The right direction of the directional pad.</summary>
    DpadRight = (1u << 7),
    /// <summary>The left shoulder (bumper) button.</summary>
    LeftShoulder = (1u << 8),
    /// <summary>The right shoulder (bumper) button.</summary>
    RightShoulder = (1u << 9),
    /// <summary>The left stick click.</summary>
    LeftStickPress = (1u << 10),
    /// <summary>The right stick click.</summary>
    RightStickPress = (1u << 11),
    /// <summary>The back / view / minus button.</summary>
    Back = (1u << 12),
    /// <summary>The start / menu / plus button.</summary>
    Start = (1u << 13),
    /// <summary>The guide / home button.</summary>
    Guide = (1u << 14),
    /// <summary>The touchpad click (DualShock 4 / DualSense); no equivalent on Xbox or Switch Pro pads.</summary>
    Touchpad = (1u << 15),
    /// <summary>The microphone mute button (DualSense); no equivalent on Xbox or Switch Pro pads.</summary>
    Mute = (1u << 16),
    /// <summary>The left rear grip paddle (Steam Controller lower-left grip / Triton L4); no equivalent on Xbox, DualSense, or Switch Pro pads.</summary>
    LeftGrip = (1u << 17),
    /// <summary>The right rear grip paddle (Steam Controller lower-right grip / Triton R4); no equivalent on Xbox, DualSense, or Switch Pro pads.</summary>
    RightGrip = (1u << 18),
    /// <summary>The second (upper) left rear grip paddle (Steam Controller Triton L5); the Triton has four rear paddles, so this pairs with <see cref="LeftGrip"/>.</summary>
    LeftUpperGrip = (1u << 19),
    /// <summary>The second (upper) right rear grip paddle (Steam Controller Triton R5); the Triton has four rear paddles, so this pairs with <see cref="RightGrip"/>.</summary>
    RightUpperGrip = (1u << 20),
    /// <summary>The Quick Access Menu (QAM "…") button (Steam Controller Triton); a second system button distinct from the <see cref="Guide"/> Steam/home button. No equivalent on Xbox, DualSense, or Switch Pro pads.</summary>
    QuickAccess = (1u << 21),
    /// <summary>The left trackpad click (Steam Controller Triton); the right trackpad click reuses <see cref="Touchpad"/>, so a device with two clickable pads reports both.</summary>
    // KEEP IN SYNC: adding a flag here requires bumping GamepadButtonEdges.Count (one press-stamp slot per
    // bit), or the coalescer faults the device on the new button's first press.
    TouchpadLeft = (1u << 22),
}
