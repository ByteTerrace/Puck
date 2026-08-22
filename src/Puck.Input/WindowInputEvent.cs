using System.Numerics;

using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// Specifies the shape of a <see cref="WindowInputEvent"/>.
/// </summary>
public enum WindowInputKind {
    /// <summary>A key transition (<see cref="WindowInputEvent.Key"/>, optional <see cref="WindowInputEvent.Character"/>), with the edge in <see cref="WindowInputEvent.Phase"/>.</summary>
    Key = 0,

    /// <summary>Typed text (<see cref="WindowInputEvent.Text"/>) — keystrokes or pasted text.</summary>
    Text,

    /// <summary>A relative pointer delta for the frame (<see cref="WindowInputEvent.Vector"/>).</summary>
    PointerMove,

    /// <summary>An absolute pointer position in client space (<see cref="WindowInputEvent.Vector"/>).</summary>
    PointerPosition,

    /// <summary>A pointer button transition (zero-based index in <see cref="WindowInputEvent.ButtonIndex"/>), with
    /// the edge in <see cref="WindowInputEvent.Phase"/>.</summary>
    PointerButton,

    /// <summary>Pointer wheel rotation in notches (<see cref="WindowInputEvent.Vector"/>; X horizontal, Y vertical).
    /// Fractional values from free-spin and precision wheels remain unquantized.</summary>
    PointerWheel,

    /// <summary>The window lost OS input focus (Alt-Tab away, click-away) — carries no other data. A platform
    /// without an equivalent event (Wayland today) never emits this kind. Never reaches
    /// <see cref="WindowInputMapper"/>: the pump intercepts it before mapping.</summary>
    FocusLost,
}
/// <summary>
/// Specifies the modifier keys held alongside a <see cref="WindowInputEvent"/> (bitwise-combinable).
/// </summary>
[Flags]
public enum WindowInputModifiers {
    /// <summary>No modifier held.</summary>
    None = 0,

    /// <summary>Either Control key held.</summary>
    Control = 1,

    /// <summary>Either Shift key held.</summary>
    Shift = 2,

    /// <summary>Either Alt key held.</summary>
    Alt = 4,

    /// <summary>Either Super (Windows / Command) key held.</summary>
    Super = 8,
}
/// <summary>
/// A provider-neutral window input event: what a native window emits after translating raw OS keys and pointer
/// motion, <em>before</em> the <see cref="InputSources"/> vocabulary and command bindings are applied. The
/// mapping to an <see cref="InputSignal"/> lives in <see cref="WindowInputMapper"/>, so the platform layer
/// never names a control — the keyboard/mouse mirror of how the gamepad transport hands up a neutral state.
/// </summary>
/// <param name="Kind">Which shape this event is.</param>
/// <param name="Key">The neutral key for a <see cref="WindowInputKind.Key"/> event; <see cref="KeyCode.None"/> otherwise.</param>
/// <param name="Character">The letter for a <see cref="KeyCode.Letter"/> event; <c>'\0'</c> otherwise.</param>
/// <param name="Text">The typed or pasted text for a <see cref="WindowInputKind.Text"/> event; <see langword="null"/> otherwise.</param>
/// <param name="Vector">The relative delta (<see cref="WindowInputKind.PointerMove"/>), absolute position (<see cref="WindowInputKind.PointerPosition"/>), or two-axis wheel notches (<see cref="WindowInputKind.PointerWheel"/>); <see cref="Vector2.Zero"/> otherwise.</param>
/// <param name="Phase">The transition the event represents: <see cref="CommandPhase.Started"/> for a key-down or pointer button-down, <see cref="CommandPhase.Completed"/> for a key-up or pointer button-up, <see cref="CommandPhase.Active"/> for a pointer move/position/wheel.</param>
/// <param name="Modifiers">The modifier keys held when the event fired; <see cref="WindowInputModifiers.None"/> unless a platform stamps it (today only the letter-key path — see <see cref="LetterDown"/>).</param>
/// <param name="DeviceId">The physical keyboard/text device that produced the event. The default value preserves
/// aggregate-keyboard platforms; Raw Input backends stamp distinct ids.</param>
/// <param name="ButtonIndex">The zero-based button index for <see cref="WindowInputKind.PointerButton"/>, or -1
/// otherwise. The range is intentionally not capped at the conventional five buttons.</param>
public readonly record struct WindowInputEvent(
    WindowInputKind Kind,
    KeyCode Key = KeyCode.None,
    char Character = '\0',
    string? Text = null,
    Vector2 Vector = default,
    CommandPhase Phase = CommandPhase.Started,
    WindowInputModifiers Modifiers = WindowInputModifiers.None,
    InputDeviceId DeviceId = default,
    int ButtonIndex = -1
) {
    /// <summary>A neutral OS-focus-lost notification.</summary>
    public static WindowInputEvent FocusLost() {
        return new WindowInputEvent(Kind: WindowInputKind.FocusLost);
    }
    /// <summary>A neutral named-key press (<see cref="CommandPhase.Started"/>).</summary>
    public static WindowInputEvent KeyDown(KeyCode key, InputDeviceId deviceId = default) {
        return new WindowInputEvent(
            Kind: WindowInputKind.Key,
            Key: key,
            Phase: CommandPhase.Started,
            DeviceId: deviceId
        );
    }
    /// <summary>A neutral named-key release (<see cref="CommandPhase.Completed"/>).</summary>
    public static WindowInputEvent KeyUp(KeyCode key, InputDeviceId deviceId = default) {
        return new WindowInputEvent(
            Kind: WindowInputKind.Key,
            Key: key,
            Phase: CommandPhase.Completed,
            DeviceId: deviceId
        );
    }
    /// <summary>A neutral letter-key press.</summary>
    public static WindowInputEvent LetterDown(char character, InputDeviceId deviceId = default) {
        return new WindowInputEvent(
            Kind: WindowInputKind.Key,
            Key: KeyCode.Letter,
            Character: character,
            Phase: CommandPhase.Started,
            DeviceId: deviceId
        );
    }
    /// <summary>A neutral letter-key release.</summary>
    public static WindowInputEvent LetterUp(char character, InputDeviceId deviceId = default) {
        return new WindowInputEvent(
            Kind: WindowInputKind.Key,
            Key: KeyCode.Letter,
            Character: character,
            Phase: CommandPhase.Completed,
            DeviceId: deviceId
        );
    }
    /// <summary>A neutral absolute pointer position, attributed to the physical pointer device that produced it.</summary>
    public static WindowInputEvent PointerAbsolute(Vector2 position, InputDeviceId deviceId = default) {
        return new WindowInputEvent(
            Kind: WindowInputKind.PointerPosition,
            Vector: position,
            Phase: CommandPhase.Active,
            DeviceId: deviceId
        );
    }
    /// <summary>A neutral pointer-button transition (0=left, 1=right, 2=middle, larger values are extra buttons), with the edge in <paramref name="phase"/>
    /// (<see cref="CommandPhase.Started"/> for down, <see cref="CommandPhase.Completed"/> for up — the same convention
    /// as <see cref="KeyDown"/>/<see cref="KeyUp"/>).</summary>
    public static WindowInputEvent PointerButton(int button, CommandPhase phase, InputDeviceId deviceId = default) {
        ArgumentOutOfRangeException.ThrowIfNegative(button);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: button,
            other: ushort.MaxValue
        );

        return new WindowInputEvent(
            Kind: WindowInputKind.PointerButton,
            Phase: phase,
            DeviceId: deviceId,
            ButtonIndex: button
        );
    }
    /// <summary>A neutral relative pointer delta (the frame's summed motion), attributed to the physical pointer
    /// device that produced it.</summary>
    public static WindowInputEvent PointerDelta(Vector2 delta, InputDeviceId deviceId = default) {
        return new WindowInputEvent(
            Kind: WindowInputKind.PointerMove,
            Vector: delta,
            Phase: CommandPhase.Active,
            DeviceId: deviceId
        );
    }
    /// <summary>A neutral pointer wheel rotation, in notches (positive away from the user).</summary>
    public static WindowInputEvent PointerWheel(float notches, InputDeviceId deviceId = default) {
        return PointerWheel(
            notches: new Vector2(
            x: 0f,
            y: notches
        ),
            deviceId: deviceId
        );
    }
    /// <summary>A neutral two-axis pointer wheel rotation, in notches (positive X is right; positive Y is away).</summary>
    public static WindowInputEvent PointerWheel(Vector2 notches, InputDeviceId deviceId = default) {
        return new WindowInputEvent(
            Kind: WindowInputKind.PointerWheel,
            Vector: notches,
            Phase: CommandPhase.Active,
            DeviceId: deviceId
        );
    }
    /// <summary>A neutral typed-text event.</summary>
    public static WindowInputEvent TypedText(string text, InputDeviceId deviceId = default) {
        ArgumentNullException.ThrowIfNull(text);

        return new WindowInputEvent(
            Kind: WindowInputKind.Text,
            Text: text,
            DeviceId: deviceId
        );
    }
}
