using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// Maps a provider-neutral <see cref="WindowInputEvent"/> to the <see cref="InputSignal"/> the command system
/// consumes, applying the <see cref="InputSources.Keyboard"/> / <see cref="InputSources.Pointer"/> vocabulary.
/// The native windows produce only raw-key → neutral-key translation; this is the single place the
/// keyboard/mouse seam names a control, mirroring how <see cref="GamepadInputSource"/> owns the gamepad
/// vocabulary. The keyboard <see cref="InputDeviceId"/> stays <see langword="default"/>, as the windows never set one.
/// </summary>
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
                    ? InputSignal.Release(source: source, modifiers: inputEvent.Modifiers)
                    : InputSignal.Press(source: source, modifiers: inputEvent.Modifiers));
            case WindowInputKind.Text:
                return InputSignal.Typed(source: InputSources.Keyboard.Text, text: (inputEvent.Text ?? string.Empty));
            case WindowInputKind.PointerMove:
                return InputSignal.Axis(source: InputSources.Pointer.Move, value: inputEvent.Vector);
            case WindowInputKind.PointerPosition:
                return InputSignal.Axis(source: InputSources.Pointer.Position, value: inputEvent.Vector);
            case WindowInputKind.PointerButton:
                // Deliberately inert: see InputSources.Pointer.Button. A drag needs continuous per-frame held
                // state, not a one-shot bound command, and button state is presentation/session-only — it must
                // never reach a CommandSnapshot. UI-layer consumers (the demo's pointer store) read button state
                // directly off the raw WindowInputEvent stream via IPointerInputSink, not through this signal or
                // the binding vocabulary. This case exists only so a click does not throw as it passes through
                // here on its way to that store.
                return new InputSignal(
                    Source: InputSources.Pointer.Button,
                    DeviceId: default,
                    Value: CommandValue.Digital(active: (inputEvent.Phase == CommandPhase.Started)),
                    Phase: inputEvent.Phase
                );
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
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(inputEvent)),
        };
    }
}
