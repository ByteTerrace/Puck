using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// The provider-neutral identifiers for <em>physical</em> input controls — the <see cref="InputSignal.Source"/>
/// values the platform emits and a binding table maps to commands. They name controls, not intents: a key is
/// <c>keyboard.escape</c>, never <c>quit</c> (that's a binding's job). Centralizing them keeps the platform
/// emitters and the app binding tables on one vocabulary instead of scattered magic strings.
/// </summary>
/// <remarks>The keyboard and the gamepad are the whole vocabulary: the POINTER has no entry here, and none should
/// be added. Cursor motion, absolute position, and held mouse buttons are browsing state — presentation/session-only,
/// continuous rather than edge-shaped, and never an input a <c>CommandSnapshot</c> carries. Consumers read that state
/// from the raw window-input stream (<see cref="IWindowInputObserver"/>), and a pointer act enters the simulation
/// only by dispatching an ordinary console verb, the same door a typed line uses. Giving the pointer a source name
/// here would be offering a binding table something it must never be allowed to bind.</remarks>
public static class InputSources {
    /// <summary>
    /// Keyboard controls. The OS-modifier keys (Control/Shift/Alt/Super) each carry distinct left/right ids —
    /// there is no unified "either side" source — so a chord group that means either declares both.
    /// </summary>
    public static class Keyboard {
        /// <summary>The left Alt key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string AltLeft = "keyboard.altLeft";
        /// <summary>The right Alt key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string AltRight = "keyboard.altRight";
        /// <summary>The Down Arrow key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ArrowDown = "keyboard.arrowDown";
        /// <summary>The Left Arrow key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ArrowLeft = "keyboard.arrowLeft";
        /// <summary>The Right Arrow key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ArrowRight = "keyboard.arrowRight";
        /// <summary>The Up Arrow key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ArrowUp = "keyboard.arrowUp";
        /// <summary>The Backspace key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Backspace = "keyboard.backspace";
        /// <summary>The backtick key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Backtick = "keyboard.backtick";
        /// <summary>The left Control key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ControlLeft = "keyboard.controlLeft";
        /// <summary>The right Control key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ControlRight = "keyboard.controlRight";
        /// <summary>The Enter key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Enter = "keyboard.enter";
        /// <summary>The Escape key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Escape = "keyboard.escape";
        /// <summary>The left Shift key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ShiftLeft = "keyboard.shiftLeft";
        /// <summary>The right Shift key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ShiftRight = "keyboard.shiftRight";
        /// <summary>The Space key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Space = "keyboard.space";
        /// <summary>The left Super (Windows / Command) key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string SuperLeft = "keyboard.superLeft";
        /// <summary>The right Super (Windows / Command) key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string SuperRight = "keyboard.superRight";
        /// <summary>The Tab key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Tab = "keyboard.tab";
        /// <summary>
        /// Text entered through the platform text-input channel. Declares <see cref="CommandValueKind.Digital"/>
        /// like a keypress (see <see cref="Puck.Commands.InputSignal.Typed"/>), but the text itself rides a
        /// separate payload no fixed <c>(valueX, valueY)</c> record can hold, so
        /// <see cref="InputSourceUnaddressableAttribute"/> excludes it regardless of that declared kind — the
        /// reason is the payload, not the shape.
        /// </summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        [InputSourceUnaddressable]
        public const string Text = "keyboard.text";

        /// <summary>The source for function key F<paramref name="number"/> (F1, F2, …).</summary>
        public static string Function(int number) {
            return $"keyboard.f{number}";
        }
        /// <summary>The source for a letter key. A chord like <c>Ctrl+C</c> is a <see cref="Puck.Commands.BindingModifierDefinition"/>
        /// declaring <see cref="ControlLeft"/> (or <see cref="ControlRight"/>) alongside this source — modifiers are ordinary
        /// declared sources, not a mechanism this key pairs with.</summary>
        public static string Letter(char letter) {
            return $"keyboard.{char.ToLowerInvariant(c: letter)}";
        }
    }

    /// <summary>
    /// Game controller controls, named with the platform-neutral South/East/West/North face-button
    /// vocabulary so a binding need not care whether the device is an Xbox, PlayStation, or
    /// Switch pad. Sticks are two-dimensional axes, triggers one-dimensional, the motion sensor a
    /// three-dimensional axis (angular velocity), and the fused pose an orientation.
    /// </summary>
    public static class Gamepad {
        /// <summary>The south face button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ButtonSouth = "gamepad.buttonSouth";
        /// <summary>The east face button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ButtonEast = "gamepad.buttonEast";
        /// <summary>The west face button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ButtonWest = "gamepad.buttonWest";
        /// <summary>The north face button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string ButtonNorth = "gamepad.buttonNorth";
        /// <summary>The upward direction on the directional pad.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string DpadUp = "gamepad.dpadUp";
        /// <summary>The downward direction on the directional pad.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string DpadDown = "gamepad.dpadDown";
        /// <summary>The left direction on the directional pad.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string DpadLeft = "gamepad.dpadLeft";
        /// <summary>The right direction on the directional pad.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string DpadRight = "gamepad.dpadRight";
        /// <summary>The left shoulder button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string LeftShoulder = "gamepad.leftShoulder";
        /// <summary>The right shoulder button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string RightShoulder = "gamepad.rightShoulder";
        /// <summary>The left stick press.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string LeftStickPress = "gamepad.leftStickPress";
        /// <summary>The right stick press.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string RightStickPress = "gamepad.rightStickPress";
        /// <summary>The back, select, or create button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Back = "gamepad.back";
        /// <summary>The start, menu, or options button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Start = "gamepad.start";
        /// <summary>The platform guide or home button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Guide = "gamepad.guide";
        /// <summary>The touchpad click button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Touchpad = "gamepad.touchpad";
        /// <summary>The microphone mute button.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Mute = "gamepad.mute";
        /// <summary>The left rear grip paddle (Steam Controller lower-left grip / Triton L5).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string LeftGrip = "gamepad.leftGrip";
        /// <summary>The right rear grip paddle (Steam Controller lower-right grip / Triton R5).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string RightGrip = "gamepad.rightGrip";
        /// <summary>The second (upper) left rear grip paddle (Steam Controller Triton L4).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string LeftUpperGrip = "gamepad.leftUpperGrip";
        /// <summary>The second (upper) right rear grip paddle (Steam Controller Triton R4).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string RightUpperGrip = "gamepad.rightUpperGrip";
        /// <summary>The Quick Access Menu (QAM) button (Steam Controller Triton).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string QuickAccess = "gamepad.quickAccess";
        /// <summary>The left trackpad click (Steam Controller Triton); the right trackpad click reuses <see cref="Touchpad"/>.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string TouchpadLeft = "gamepad.touchpadLeft";
        /// <summary>The first touch contact on the touchpad.</summary>
        [InputSourceValue(kind: CommandValueKind.Axis2D)]
        public const string Touchpad0 = "gamepad.touchpad0";
        /// <summary>The second touch contact on the touchpad.</summary>
        [InputSourceValue(kind: CommandValueKind.Axis2D)]
        public const string Touchpad1 = "gamepad.touchpad1";
        /// <summary>The two-dimensional left stick axis.</summary>
        [InputSourceValue(kind: CommandValueKind.Axis2D)]
        public const string LeftStick = "gamepad.leftStick";
        /// <summary>The two-dimensional right stick axis.</summary>
        [InputSourceValue(kind: CommandValueKind.Axis2D)]
        public const string RightStick = "gamepad.rightStick";
        /// <summary>The left trigger axis.</summary>
        [InputSourceValue(kind: CommandValueKind.Axis1D)]
        public const string LeftTrigger = "gamepad.leftTrigger";
        /// <summary>The right trigger axis.</summary>
        [InputSourceValue(kind: CommandValueKind.Axis1D)]
        public const string RightTrigger = "gamepad.rightTrigger";
        /// <summary>
        /// The three-dimensional angular-velocity signal. Its <see cref="CommandValueKind.Axis3D"/> kind carries
        /// one more component than an addon record's <c>(valueX, valueY)</c> pair holds, so it resolves as
        /// unaddressable through its kind alone (see <see cref="InputSourceValueAttribute"/>'s remarks).
        /// </summary>
        [InputSourceValue(kind: CommandValueKind.Axis3D)]
        public const string Gyro = "gamepad.gyro";
        /// <summary>The three-dimensional acceleration signal. See <see cref="Gyro"/>'s remarks on why its kind alone makes it unaddressable.</summary>
        [InputSourceValue(kind: CommandValueKind.Axis3D)]
        public const string Accelerometer = "gamepad.accelerometer";
        /// <summary>The fused device orientation. Its <see cref="CommandValueKind.Orientation"/> kind carries a whole quaternion, so it too resolves as unaddressable through its kind alone.</summary>
        [InputSourceValue(kind: CommandValueKind.Orientation)]
        public const string Orientation = "gamepad.orientation";
    }
}
