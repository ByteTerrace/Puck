using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// Maps a provider-neutral <see cref="WindowInputEvent"/> to the <see cref="InputSignal"/> the command system
/// consumes, applying the <see cref="InputSources.Keyboard"/> and <see cref="InputSources.Mouse"/> vocabularies.
/// The native windows produce only raw-key → neutral-key translation, while the shared map is the single place the
/// window seam names a control, mirroring how <see cref="GamepadCaptureSource"/> owns the gamepad vocabulary. The
/// event's <see cref="InputDeviceId"/> is preserved so device-aware backends route independently.
/// </summary>
/// <remarks>Absolute position is the one pointer shape that remains presentation-only. Relative motion, buttons,
/// and wheel motion are projected into bindable mouse sources while the raw event independently reaches
/// <see cref="IWindowInputObserver"/>.</remarks>
public static class WindowInputMapper {
    /// <summary>Translates one neutral window event into its corresponding input signal.</summary>
    /// <param name="inputEvent">The neutral window event to translate.</param>
    /// <returns>The input signal carrying the event's vocabulary, value, and phase.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The event's <see cref="WindowInputEvent.Kind"/> or <see cref="WindowInputEvent.Key"/> is unrecognized.</exception>
    public static InputSignal ToInputSignal(in WindowInputEvent inputEvent) {
        switch (inputEvent.Kind) {
            case WindowInputKind.Key:
                if (!KeyboardSourceMap.TryGetSource(
                    character: inputEvent.Character,
                    key: inputEvent.Key,
                    source: out var source
                )) {
                    throw new ArgumentOutOfRangeException(paramName: nameof(inputEvent));
                }

                return ((inputEvent.Phase == CommandPhase.Completed)
                    ? InputSignal.Release(
                        source: source,
                        deviceId: inputEvent.DeviceId
                    )
                    : InputSignal.Press(
                        source: source,
                        deviceId: inputEvent.DeviceId
                    )
                );
            case WindowInputKind.Text:
                return InputSignal.Typed(
                    source: InputSources.Keyboard.Text,
                    text: (inputEvent.Text ?? string.Empty),
                    deviceId: inputEvent.DeviceId
                );
            case WindowInputKind.PointerMove:
                return InputSignal.Axis(
                    source: InputSources.Mouse.Motion,
                    value: inputEvent.Vector,
                    deviceId: inputEvent.DeviceId,
                    transient: true
                );
            case WindowInputKind.PointerButton:
                var buttonSource = InputSources.Mouse.Button(number: checked((inputEvent.ButtonIndex + 1)));

                return ((inputEvent.Phase is CommandPhase.Completed or CommandPhase.Canceled)
                    ? InputSignal.Release(
                        source: buttonSource,
                        deviceId: inputEvent.DeviceId
                    )
                    : InputSignal.Press(
                        source: buttonSource,
                        deviceId: inputEvent.DeviceId
                    )
                );
            case WindowInputKind.PointerWheel:
                return InputSignal.Axis(
                    source: InputSources.Mouse.Wheel,
                    value: inputEvent.Vector,
                    deviceId: inputEvent.DeviceId,
                    transient: true
                );
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(inputEvent));
        }
    }
}
