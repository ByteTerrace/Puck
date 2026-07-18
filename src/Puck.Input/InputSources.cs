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
        /// <summary>The Down Arrow key.</summary>
        public const string ArrowDown = "keyboard.arrowDown";
        /// <summary>The Left Arrow key.</summary>
        public const string ArrowLeft = "keyboard.arrowLeft";
        /// <summary>The Right Arrow key.</summary>
        public const string ArrowRight = "keyboard.arrowRight";
        /// <summary>The Up Arrow key.</summary>
        public const string ArrowUp = "keyboard.arrowUp";
        /// <summary>The Backspace key.</summary>
        public const string Backspace = "keyboard.backspace";
        /// <summary>The backtick key.</summary>
        public const string Backtick = "keyboard.backtick";
        /// <summary>The Enter key.</summary>
        public const string Enter = "keyboard.enter";
        /// <summary>The Escape key.</summary>
        public const string Escape = "keyboard.escape";
        /// <summary>The Space key.</summary>
        public const string Space = "keyboard.space";
        /// <summary>The Tab key.</summary>
        public const string Tab = "keyboard.tab";
        /// <summary>Text entered through the platform text-input channel.</summary>
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

        /// <summary>
        /// Deliberately UNBOUND — no binding table should target this. A drag needs continuous per-frame held
        /// state, not a one-shot bound command, and button state is presentation/session-only (never simulation
        /// input); consumers read it from a dedicated pointer store fed directly off the raw window-input stream
        /// (<see cref="Puck.Input.IPointerInputSink"/>), not through the binding vocabulary. Exists only so
        /// <see cref="Puck.Input.WindowInputMapper"/> can name a non-null <see cref="InputSignal.Source"/> for a
        /// button event without inventing a scattered magic string.
        /// </summary>
        public const string Button = "pointer.button";
    }

    /// <summary>
    /// Game controller controls, named with the platform-neutral South/East/West/North face-button
    /// vocabulary so a binding need not care whether the device is an Xbox, PlayStation, or
    /// Switch pad. Sticks are two-dimensional axes, triggers one-dimensional, the motion sensor a
    /// three-dimensional axis (angular velocity), and the fused pose an orientation.
    /// </summary>
    public static class Gamepad {
        /// <summary>The south face button.</summary>
        public const string ButtonSouth = "gamepad.buttonSouth";
        /// <summary>The east face button.</summary>
        public const string ButtonEast = "gamepad.buttonEast";
        /// <summary>The west face button.</summary>
        public const string ButtonWest = "gamepad.buttonWest";
        /// <summary>The north face button.</summary>
        public const string ButtonNorth = "gamepad.buttonNorth";
        /// <summary>The upward direction on the directional pad.</summary>
        public const string DpadUp = "gamepad.dpadUp";
        /// <summary>The downward direction on the directional pad.</summary>
        public const string DpadDown = "gamepad.dpadDown";
        /// <summary>The left direction on the directional pad.</summary>
        public const string DpadLeft = "gamepad.dpadLeft";
        /// <summary>The right direction on the directional pad.</summary>
        public const string DpadRight = "gamepad.dpadRight";
        /// <summary>The left shoulder button.</summary>
        public const string LeftShoulder = "gamepad.leftShoulder";
        /// <summary>The right shoulder button.</summary>
        public const string RightShoulder = "gamepad.rightShoulder";
        /// <summary>The left stick press.</summary>
        public const string LeftStickPress = "gamepad.leftStickPress";
        /// <summary>The right stick press.</summary>
        public const string RightStickPress = "gamepad.rightStickPress";
        /// <summary>The back, select, or create button.</summary>
        public const string Back = "gamepad.back";
        /// <summary>The start, menu, or options button.</summary>
        public const string Start = "gamepad.start";
        /// <summary>The platform guide or home button.</summary>
        public const string Guide = "gamepad.guide";
        /// <summary>The touchpad click button.</summary>
        public const string Touchpad = "gamepad.touchpad";
        /// <summary>The microphone mute button.</summary>
        public const string Mute = "gamepad.mute";
        /// <summary>The first touch contact on the touchpad.</summary>
        public const string Touchpad0 = "gamepad.touchpad0";
        /// <summary>The second touch contact on the touchpad.</summary>
        public const string Touchpad1 = "gamepad.touchpad1";
        /// <summary>The two-dimensional left stick axis.</summary>
        public const string LeftStick = "gamepad.leftStick";
        /// <summary>The two-dimensional right stick axis.</summary>
        public const string RightStick = "gamepad.rightStick";
        /// <summary>The left trigger axis.</summary>
        public const string LeftTrigger = "gamepad.leftTrigger";
        /// <summary>The right trigger axis.</summary>
        public const string RightTrigger = "gamepad.rightTrigger";
        /// <summary>The three-dimensional angular-velocity signal.</summary>
        public const string Gyro = "gamepad.gyro";
        /// <summary>The three-dimensional acceleration signal.</summary>
        public const string Accelerometer = "gamepad.accelerometer";
        /// <summary>The fused device orientation.</summary>
        public const string Orientation = "gamepad.orientation";
    }
}
