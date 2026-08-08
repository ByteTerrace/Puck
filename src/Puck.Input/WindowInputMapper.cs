using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// Maps a provider-neutral <see cref="WindowInputEvent"/> to the <see cref="InputSignal"/> the command system
/// consumes, applying the <see cref="InputSources.Keyboard"/> vocabulary through <see cref="KeyboardSourceMap"/>.
/// The native windows produce only raw-key → neutral-key translation, while the shared map is the single place the
/// keyboard seam names a control, mirroring how <see cref="GamepadCaptureSource"/> owns the gamepad vocabulary. The
/// keyboard <see cref="InputDeviceId"/> stays <see langword="default"/>, as the windows never set one.
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
                if (!KeyboardSourceMap.TryGetSource(key: inputEvent.Key, character: inputEvent.Character, source: out var source)) {
                    throw new ArgumentOutOfRangeException(paramName: nameof(inputEvent));
                }

                return ((inputEvent.Phase == CommandPhase.Completed)
                    ? InputSignal.Release(source: source)
                    : InputSignal.Press(source: source));
            case WindowInputKind.Text:
                return InputSignal.Typed(source: InputSources.Keyboard.Text, text: (inputEvent.Text ?? string.Empty));
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(inputEvent));
        }
    }
}
