using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// Maps a provider-neutral <see cref="WindowInputEvent"/> to the <see cref="InputSignal"/> the command system
/// consumes, applying the <see cref="InputSources.Keyboard"/> vocabulary. The native windows produce only raw-key →
/// neutral-key translation; this is the single place the keyboard seam names a control, mirroring how
/// <see cref="GamepadCaptureSource"/> owns the gamepad vocabulary. The keyboard <see cref="InputDeviceId"/> stays
/// <see langword="default"/>, as the windows never set one.
/// </summary>
/// <remarks>The pointer has no vocabulary here at all. Browsing state — cursor motion, absolute position, held
/// buttons, wheel rotation — is presentation/session-only and reaches its consumers through
/// <see cref="IWindowInputObserver"/> alone, so the window pump skips the four pointer kinds before mapping, exactly
/// as it skips <see cref="WindowInputKind.FocusLost"/>. Any of the five arriving here is a pump defect and throws
/// rather than being quietly passed through as an inert signal.</remarks>
public static class WindowInputMapper {
    /// <summary>Translates one neutral window event into its corresponding input signal.</summary>
    /// <param name="inputEvent">The neutral window event to translate.</param>
    /// <returns>The input signal carrying the event's vocabulary, value, and phase.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The event's <see cref="WindowInputEvent.Kind"/> or <see cref="WindowInputEvent.Key"/> is unrecognized.</exception>
    public static InputSignal ToInputSignal(in WindowInputEvent inputEvent) {
        switch (inputEvent.Kind) {
            case WindowInputKind.Key:
                var source = SourceFor(inputEvent: in inputEvent);

                return ((inputEvent.Phase == CommandPhase.Completed)
                    ? InputSignal.Release(source: source)
                    : InputSignal.Press(source: source));
            case WindowInputKind.Text:
                return InputSignal.Typed(source: InputSources.Keyboard.Text, text: (inputEvent.Text ?? string.Empty));
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(inputEvent));
        }
    }

    private static string SourceFor(in WindowInputEvent inputEvent) {
        return inputEvent.Key switch {
            KeyCode.Backtick => InputSources.Keyboard.Backtick,
            KeyCode.Backspace => InputSources.Keyboard.Backspace,
            KeyCode.Enter => InputSources.Keyboard.Enter,
            KeyCode.Escape => InputSources.Keyboard.Escape,
            KeyCode.Tab => InputSources.Keyboard.Tab,
            KeyCode.ArrowUp => InputSources.Keyboard.ArrowUp,
            KeyCode.ArrowDown => InputSources.Keyboard.ArrowDown,
            KeyCode.ArrowLeft => InputSources.Keyboard.ArrowLeft,
            KeyCode.ArrowRight => InputSources.Keyboard.ArrowRight,
            KeyCode.Space => InputSources.Keyboard.Space,
            KeyCode.Letter => InputSources.Keyboard.Letter(letter: inputEvent.Character),
            >= KeyCode.F1 and <= KeyCode.F12 => InputSources.Keyboard.Function(number: ((inputEvent.Key - KeyCode.F1) + 1)),
            KeyCode.ControlLeft => InputSources.Keyboard.ControlLeft,
            KeyCode.ControlRight => InputSources.Keyboard.ControlRight,
            KeyCode.ShiftLeft => InputSources.Keyboard.ShiftLeft,
            KeyCode.ShiftRight => InputSources.Keyboard.ShiftRight,
            KeyCode.AltLeft => InputSources.Keyboard.AltLeft,
            KeyCode.AltRight => InputSources.Keyboard.AltRight,
            KeyCode.SuperLeft => InputSources.Keyboard.SuperLeft,
            KeyCode.SuperRight => InputSources.Keyboard.SuperRight,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(inputEvent)),
        };
    }
}
