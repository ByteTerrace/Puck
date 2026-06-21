namespace Puck.Commands;

/// <summary>
/// The modifier keys held when an <see cref="InputSignal"/> fired. Modifiers are first-class so chords
/// (<c>Ctrl+C</c>, <c>Ctrl+Tab</c>, …) are real inputs an app binds to commands, rather than semantics the
/// platform bakes in. A <see cref="CommandBinding.RequiredModifiers"/> selects which chord a binding answers.
/// </summary>
[Flags]
public enum InputModifiers {
    /// <summary>No modifier held.</summary>
    None = 0,
    /// <summary>A Control key is held.</summary>
    Control = (1 << 0),
    /// <summary>A Shift key is held.</summary>
    Shift = (1 << 1),
    /// <summary>An Alt (Menu) key is held.</summary>
    Alt = (1 << 2),
    /// <summary>A Super (Windows / Command) key is held.</summary>
    Super = (1 << 3),
}
