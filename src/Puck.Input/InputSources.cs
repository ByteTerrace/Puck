using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// The provider-neutral identifiers for <em>physical</em> input controls — the <see cref="InputSignal.Source"/>
/// values the platform emits and a binding table maps to commands. They name controls, not intents: a key is
/// <c>keyboard.escape</c>, never <c>quit</c> (that's a binding's job). Centralizing them keeps the platform
/// emitters and the app binding tables on one vocabulary instead of scattered magic strings.
/// </summary>
/// <remarks>Absolute cursor position remains browsing state and has no source id. The mouse's command plane is
/// deliberately separate: relative motion, wheel motion, and numbered buttons are ordinary bindable controls while
/// the same raw event may also update presentation state through <see cref="IWindowInputObserver"/>.</remarks>
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
        /// <summary>The number-row minus key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Minus = "keyboard.minus";
        /// <summary>The number-row equals key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string EqualsKey = "keyboard.equals";
        /// <summary>The numpad subtract key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string NumpadSubtract = "keyboard.numpadSubtract";
        /// <summary>The numpad add key.</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string NumpadAdd = "keyboard.numpadAdd";
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
        /// <param name="number">The function-key number, from 1 through 12.</param>
        /// <returns>The physical source identifier for the function key.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is outside the range 1 through 12.</exception>
        public static string Function(int number) {
            ArgumentOutOfRangeException.ThrowIfLessThan(value: number, other: 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: number, other: 12);

            return $"keyboard.f{number}";
        }
        /// <summary>The source for a letter key. A chord like <c>Ctrl+C</c> is a <see cref="Puck.Commands.BindingModifierDefinition"/>
        /// declaring <see cref="ControlLeft"/> (or <see cref="ControlRight"/>) alongside this source — modifiers are ordinary
        /// declared sources, not a mechanism this key pairs with.</summary>
        /// <param name="letter">The ASCII letter, A through Z in either case.</param>
        /// <returns>The lowercase physical source identifier for the letter.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="letter"/> is not an ASCII letter.</exception>
        public static string Letter(char letter) {
            if (letter is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z')) {
                throw new ArgumentOutOfRangeException(paramName: nameof(letter), actualValue: letter, message: "The key must be an ASCII letter from A through Z.");
            }

            return $"keyboard.{char.ToLowerInvariant(c: letter)}";
        }
        /// <summary>Gets the source for a number-row digit.</summary>
        /// <param name="number">The digit, from 0 through 9.</param>
        public static string Digit(int number) {
            ArgumentOutOfRangeException.ThrowIfNegative(number);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 9);

            return $"keyboard.{number}";
        }
        /// <summary>Gets the source for a numpad digit.</summary>
        /// <param name="number">The digit, from 0 through 9.</param>
        public static string NumpadDigit(int number) {
            ArgumentOutOfRangeException.ThrowIfNegative(number);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 9);

            return $"keyboard.numpad{number}";
        }
    }

    /// <summary>
    /// Mouse controls. Button numbers are provider-neutral and one-based: 1 is left, 2 right, 3 middle, and every
    /// larger number preserves the backend's stable extra-button order rather than imposing a five-button ceiling.
    /// </summary>
    public static class Mouse {
        /// <summary>The left mouse button (button 1).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string LeftButton = "mouse.button1";
        /// <summary>The right mouse button (button 2).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string RightButton = "mouse.button2";
        /// <summary>The middle mouse button (button 3).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string MiddleButton = "mouse.button3";
        /// <summary>The first conventional extra mouse button (button 4).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Button4 = "mouse.button4";
        /// <summary>The second conventional extra mouse button (button 5).</summary>
        [InputSourceValue(kind: CommandValueKind.Digital)]
        public const string Button5 = "mouse.button5";
        /// <summary>Relative mouse motion in device units.</summary>
        [InputSourceValue(kind: CommandValueKind.Axis2D)]
        public const string Motion = "mouse.motion";
        /// <summary>Wheel motion in notches: X is horizontal and Y is vertical.</summary>
        [InputSourceValue(kind: CommandValueKind.Axis2D)]
        public const string Wheel = "mouse.wheel";

        /// <summary>Gets the source id for a numbered mouse button.</summary>
        /// <param name="number">The one-based button number, from 1 through 65535.</param>
        /// <returns>The provider-neutral button source id.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is outside 1 through 65535.</exception>
        public static string Button(int number) {
            ArgumentOutOfRangeException.ThrowIfLessThan(value: number, other: 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: number, other: ushort.MaxValue);

            return $"mouse.button{number}";
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
