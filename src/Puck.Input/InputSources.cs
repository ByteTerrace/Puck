using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// The provider-neutral identifiers for <em>physical</em> input controls — the <see cref="InputSignal.Source"/>
/// values the platform emits and a binding table maps to commands. They name controls, not intents: a key is
/// <c>keyboard.escape</c>, never <c>quit</c> (that's a binding's job). Centralizing them keeps the platform
/// emitters and the app binding tables on one vocabulary instead of scattered magic strings.
/// </summary>
public static class InputSources {
    /// <summary>Keyboard controls.</summary>
    public static class Keyboard {
        public const string ArrowDown = "keyboard.arrowDown";
        public const string ArrowLeft = "keyboard.arrowLeft";
        public const string ArrowRight = "keyboard.arrowRight";
        public const string ArrowUp = "keyboard.arrowUp";
        public const string Backspace = "keyboard.backspace";
        public const string Backtick = "keyboard.backtick";
        public const string Enter = "keyboard.enter";
        public const string Escape = "keyboard.escape";
        public const string Tab = "keyboard.tab";
        public const string Text = "keyboard.text";

        /// <summary>The source for function key F<paramref name="number"/> (F1, F2, …).</summary>
        public static string Function(int number) {
            return $"keyboard.f{number}";
        }
        /// <summary>The source for a letter key (chords like <c>Ctrl+C</c> pair this with a modifier).</summary>
        public static string Letter(char letter) {
            return $"keyboard.{char.ToLowerInvariant(c: letter)}";
        }
    }

    /// <summary>Pointer (mouse) controls.</summary>
    public static class Pointer {
        /// <summary>The relative pointer motion delta for this frame (raw, un-accelerated; summed across the frame).</summary>
        public const string Move = "pointer.move";
        /// <summary>The absolute pointer position in client space (for an OS-style cursor or hit-testing).</summary>
        public const string Position = "pointer.position";
    }

    /// <summary>
    /// Game controller controls, named with the platform-neutral South/East/West/North face-button
    /// vocabulary (SDL convention) so a binding need not care whether the device is an Xbox, PlayStation, or
    /// Switch pad. Sticks are two-dimensional axes, triggers one-dimensional, the motion sensor a
    /// three-dimensional axis (angular velocity), and the fused pose an orientation.
    /// </summary>
    public static class Gamepad {
        public const string ButtonSouth = "gamepad.buttonSouth";
        public const string ButtonEast = "gamepad.buttonEast";
        public const string ButtonWest = "gamepad.buttonWest";
        public const string ButtonNorth = "gamepad.buttonNorth";
        public const string DpadUp = "gamepad.dpadUp";
        public const string DpadDown = "gamepad.dpadDown";
        public const string DpadLeft = "gamepad.dpadLeft";
        public const string DpadRight = "gamepad.dpadRight";
        public const string LeftShoulder = "gamepad.leftShoulder";
        public const string RightShoulder = "gamepad.rightShoulder";
        public const string LeftStickPress = "gamepad.leftStickPress";
        public const string RightStickPress = "gamepad.rightStickPress";
        public const string Back = "gamepad.back";
        public const string Start = "gamepad.start";
        public const string Guide = "gamepad.guide";
        public const string Touchpad = "gamepad.touchpad";
        public const string Mute = "gamepad.mute";
        public const string Touchpad0 = "gamepad.touchpad0";
        public const string Touchpad1 = "gamepad.touchpad1";
        public const string LeftStick = "gamepad.leftStick";
        public const string RightStick = "gamepad.rightStick";
        public const string LeftTrigger = "gamepad.leftTrigger";
        public const string RightTrigger = "gamepad.rightTrigger";
        public const string Gyro = "gamepad.gyro";
        public const string Accelerometer = "gamepad.accelerometer";
        public const string Orientation = "gamepad.orientation";
    }
}
