using Puck.Input.Devices;

namespace Puck.Input;

/// <summary>
/// Names physical input sources the way the player's own hardware does, so a prompt can say "press B" to a
/// Switch Pro holder, "press A" to an Xbox or Steam holder, and "press X" to a DualSense holder instead of
/// leaking the engine's neutral position vocabulary. <see cref="Describe"/> gives the family-specific short
/// label; <see cref="DescribePosition"/> gives the family-neutral spoken form ("the south face button") for a
/// prompt that must work before any pad is connected. Pure string mapping — the text mirror of the binding
/// bar's <c>BindingGlyphResolver</c> shader glyphs.
/// </summary>
public static class InputSourceLabels {
    /// <summary>Describes a source with the connected family's own vocabulary (face letters/shapes, L1 vs LB vs ZL, Options vs Menu vs Plus).</summary>
    /// <param name="source">The provider-neutral input source id (an <see cref="InputSources"/> control).</param>
    /// <param name="family">The connected controller family; <see cref="GamepadType.Unknown"/> uses Xbox vocabulary.</param>
    /// <returns>The short label, or the source id itself when it is not a control this vocabulary names.</returns>
    public static string Describe(string source, GamepadType family) {
        return source switch {
            InputSources.Gamepad.ButtonSouth or InputSources.Gamepad.ButtonEast or InputSources.Gamepad.ButtonWest or InputSources.Gamepad.ButtonNorth => DescribeFace(source: source, family: family),
            InputSources.Gamepad.DpadUp => "D-pad Up",
            InputSources.Gamepad.DpadDown => "D-pad Down",
            InputSources.Gamepad.DpadLeft => "D-pad Left",
            InputSources.Gamepad.DpadRight => "D-pad Right",
            InputSources.Gamepad.LeftShoulder => family switch {
                GamepadType.SwitchPro => "L",
                GamepadType.PlayStation5 or GamepadType.SteamController or GamepadType.SteamControllerTriton => "L1",
                _ => "LB",
            },
            InputSources.Gamepad.RightShoulder => family switch {
                GamepadType.SwitchPro => "R",
                GamepadType.PlayStation5 or GamepadType.SteamController or GamepadType.SteamControllerTriton => "R1",
                _ => "RB",
            },
            InputSources.Gamepad.LeftTrigger => family switch {
                GamepadType.SwitchPro => "ZL",
                GamepadType.PlayStation5 or GamepadType.SteamController or GamepadType.SteamControllerTriton => "L2",
                _ => "LT",
            },
            InputSources.Gamepad.RightTrigger => family switch {
                GamepadType.SwitchPro => "ZR",
                GamepadType.PlayStation5 or GamepadType.SteamController or GamepadType.SteamControllerTriton => "R2",
                _ => "RT",
            },
            InputSources.Gamepad.LeftStickPress => family switch {
                GamepadType.PlayStation5 or GamepadType.SteamController or GamepadType.SteamControllerTriton => "L3",
                _ => "LS",
            },
            InputSources.Gamepad.RightStickPress => family switch {
                GamepadType.PlayStation5 or GamepadType.SteamController or GamepadType.SteamControllerTriton => "R3",
                _ => "RS",
            },
            InputSources.Gamepad.Start => family switch {
                GamepadType.SwitchPro => "Plus",
                GamepadType.PlayStation5 => "Options",
                _ => "Menu",
            },
            InputSources.Gamepad.Back => family switch {
                GamepadType.SwitchPro => "Minus",
                GamepadType.PlayStation5 => "Create",
                _ => "View",
            },
            InputSources.Gamepad.Guide => family switch {
                GamepadType.SwitchPro => "Home",
                GamepadType.PlayStation5 => "PS",
                GamepadType.SteamController or GamepadType.SteamControllerTriton => "Steam",
                _ => "Guide",
            },
            InputSources.Gamepad.Touchpad => family switch {
                GamepadType.SteamController or GamepadType.SteamControllerTriton => "Right Trackpad (click)",
                _ => "Touchpad (click)",
            },
            InputSources.Gamepad.Mute => "Mute",
            InputSources.Gamepad.Touchpad0 => family switch {
                GamepadType.SteamController or GamepadType.SteamControllerTriton => "Right Trackpad",
                _ => "Touchpad (finger 1)",
            },
            InputSources.Gamepad.Touchpad1 => family switch {
                GamepadType.SteamController or GamepadType.SteamControllerTriton => "Left Trackpad",
                _ => "Touchpad (finger 2)",
            },
            InputSources.Gamepad.LeftStick => "Left Stick",
            InputSources.Gamepad.RightStick => "Right Stick",
            InputSources.Gamepad.Gyro => "Motion",
            InputSources.Gamepad.Accelerometer => "Motion",
            InputSources.Gamepad.Orientation => "Motion",
            _ => source,
        };
    }

    /// <summary>Describes a source in the family-neutral spoken form ("the south face button"), for prompts that must read correctly on any — or no — connected pad.</summary>
    /// <param name="source">The provider-neutral input source id (an <see cref="InputSources"/> control).</param>
    /// <returns>The spoken description, or the source id itself when it is not a control this vocabulary names.</returns>
    public static string DescribePosition(string source) {
        return source switch {
            InputSources.Gamepad.ButtonSouth => "the south face button",
            InputSources.Gamepad.ButtonEast => "the east face button",
            InputSources.Gamepad.ButtonWest => "the west face button",
            InputSources.Gamepad.ButtonNorth => "the north face button",
            InputSources.Gamepad.DpadUp => "D-pad up",
            InputSources.Gamepad.DpadDown => "D-pad down",
            InputSources.Gamepad.DpadLeft => "D-pad left",
            InputSources.Gamepad.DpadRight => "D-pad right",
            InputSources.Gamepad.LeftShoulder => "the left bumper",
            InputSources.Gamepad.RightShoulder => "the right bumper",
            InputSources.Gamepad.LeftTrigger => "the left trigger",
            InputSources.Gamepad.RightTrigger => "the right trigger",
            InputSources.Gamepad.LeftStickPress => "the left stick (pressed in)",
            InputSources.Gamepad.RightStickPress => "the right stick (pressed in)",
            InputSources.Gamepad.Start => "the start button",
            InputSources.Gamepad.Back => "the back button",
            InputSources.Gamepad.Guide => "the guide button",
            InputSources.Gamepad.Touchpad => "the touchpad (clicked)",
            InputSources.Gamepad.Mute => "the mute button",
            InputSources.Gamepad.Touchpad0 => "the touchpad (first finger)",
            InputSources.Gamepad.Touchpad1 => "the touchpad (second finger)",
            InputSources.Gamepad.LeftStick => "the left stick",
            InputSources.Gamepad.RightStick => "the right stick",
            InputSources.Gamepad.Gyro or InputSources.Gamepad.Accelerometer or InputSources.Gamepad.Orientation => "the motion sensor",
            _ => source,
        };
    }

    // The face letters/shapes per family. Nintendo's letters sit in different positions than Xbox's (B is South
    // and A is East), and the DualSense names its buttons by shape — the whole reason prompts must resolve
    // through the connected family instead of hardcoding one vendor's letters.
    private static string DescribeFace(string source, GamepadType family) {
        if (family == GamepadType.SwitchPro) {
            return source switch {
                InputSources.Gamepad.ButtonSouth => "B",
                InputSources.Gamepad.ButtonEast => "A",
                InputSources.Gamepad.ButtonWest => "Y",
                _ => "X",
            };
        }

        if (family == GamepadType.PlayStation5) {
            return source switch {
                InputSources.Gamepad.ButtonSouth => "X",
                InputSources.Gamepad.ButtonEast => "O",
                InputSources.Gamepad.ButtonWest => "Square",
                _ => "Triangle",
            };
        }

        // Xbox, Steam (both generations), and unknown devices: A South, B East, X West, Y North.
        return source switch {
            InputSources.Gamepad.ButtonSouth => "A",
            InputSources.Gamepad.ButtonEast => "B",
            InputSources.Gamepad.ButtonWest => "X",
            _ => "Y",
        };
    }
}
