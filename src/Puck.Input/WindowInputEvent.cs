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
/// <param name="Vector">The relative delta (<see cref="WindowInputKind.PointerMove"/>) or absolute position (<see cref="WindowInputKind.PointerPosition"/>); <see cref="Vector2.Zero"/> otherwise.</param>
/// <param name="Modifiers">The modifier keys held when the event fired (for chords); <see cref="InputModifiers.None"/> when the backend reports none.</param>
/// <param name="Phase">The transition the event represents: <see cref="CommandPhase.Started"/> for a key-down, <see cref="CommandPhase.Completed"/> for a key-up, <see cref="CommandPhase.Active"/> for pointer events.</param>
public readonly record struct WindowInputEvent(
    WindowInputKind Kind,
    KeyCode Key = KeyCode.None,
    char Character = '\0',
    string? Text = null,
    Vector2 Vector = default,
    InputModifiers Modifiers = InputModifiers.None,
    CommandPhase Phase = CommandPhase.Started
) {
    /// <summary>A neutral named-key press (<see cref="CommandPhase.Started"/>).</summary>
    public static WindowInputEvent KeyDown(KeyCode key, InputModifiers modifiers = InputModifiers.None) {
        return new WindowInputEvent(Kind: WindowInputKind.Key, Key: key, Modifiers: modifiers, Phase: CommandPhase.Started);
    }
    /// <summary>A neutral named-key release (<see cref="CommandPhase.Completed"/>).</summary>
    public static WindowInputEvent KeyUp(KeyCode key, InputModifiers modifiers = InputModifiers.None) {
        return new WindowInputEvent(Kind: WindowInputKind.Key, Key: key, Modifiers: modifiers, Phase: CommandPhase.Completed);
    }
    /// <summary>A neutral letter-key press (chords pair this with a modifier).</summary>
    public static WindowInputEvent LetterDown(char character, InputModifiers modifiers = InputModifiers.None) {
        return new WindowInputEvent(Kind: WindowInputKind.Key, Key: KeyCode.Letter, Character: character, Modifiers: modifiers, Phase: CommandPhase.Started);
    }
    /// <summary>A neutral letter-key release.</summary>
    public static WindowInputEvent LetterUp(char character, InputModifiers modifiers = InputModifiers.None) {
        return new WindowInputEvent(Kind: WindowInputKind.Key, Key: KeyCode.Letter, Character: character, Modifiers: modifiers, Phase: CommandPhase.Completed);
    }
    /// <summary>A neutral typed-text event.</summary>
    public static WindowInputEvent TypedText(string text) {
        ArgumentNullException.ThrowIfNull(text);

        return new WindowInputEvent(Kind: WindowInputKind.Text, Text: text);
    }
    /// <summary>A neutral relative pointer delta (the frame's summed motion).</summary>
    public static WindowInputEvent PointerDelta(Vector2 delta) {
        return new WindowInputEvent(Kind: WindowInputKind.PointerMove, Vector: delta, Phase: CommandPhase.Active);
    }
    /// <summary>A neutral absolute pointer position.</summary>
    public static WindowInputEvent PointerAbsolute(Vector2 position) {
        return new WindowInputEvent(Kind: WindowInputKind.PointerPosition, Vector: position, Phase: CommandPhase.Active);
    }
}
