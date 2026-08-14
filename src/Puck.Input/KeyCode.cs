namespace Puck.Input;

/// <summary>
/// Specifies a provider-neutral keyboard key. The OS-specific native windows translate their raw key codes
/// (Win32 virtual keys, X11/evdev keycodes) into these, and the shared keyboard source map maps each to its
/// <see cref="InputSources.Keyboard"/> vocabulary entry — the single place a key is named. Named keys only:
/// letter keys ride <see cref="WindowInputEvent.Character"/>, and typed text rides
/// <see cref="WindowInputEvent.Text"/>. Number-row and numpad digits are distinct named controls. Control/Shift/Alt/Super each carry distinct left/right members rather
/// than one unified key — "either side" is an authoring choice a chord group expresses by declaring both, not a
/// distinction the vocabulary collapses for them.
/// </summary>
public enum KeyCode {
    /// <summary>No key; the default, used by non-key events.</summary>
    None = 0,

    /// <summary>The backtick / grave key (the console toggle).</summary>
    Backtick,

    /// <summary>The Backspace key.</summary>
    Backspace,

    /// <summary>The Enter / Return key.</summary>
    Enter,

    /// <summary>The Escape key.</summary>
    Escape,

    /// <summary>The Tab key.</summary>
    Tab,

    /// <summary>The Up arrow key.</summary>
    ArrowUp,

    /// <summary>The Down arrow key.</summary>
    ArrowDown,

    /// <summary>The Left arrow key.</summary>
    ArrowLeft,

    /// <summary>The Right arrow key.</summary>
    ArrowRight,

    /// <summary>The Space bar — a named key (like Enter) so a binding can target it as a first-class control (the world's
    /// jump action rides it). Its WM_CHAR (a literal space) still flows to the text pipeline independently, exactly as a
    /// letter key's does, so typed text is unaffected.</summary>
    Space,

    /// <summary>A letter key; the specific letter is carried by <see cref="WindowInputEvent.Character"/>.</summary>
    Letter,

    /// <summary>The number-row 0 key. Digit0 through Digit9 are contiguous.</summary>
    Digit0,
    /// <summary>The number-row 1 key.</summary>
    Digit1,
    /// <summary>The number-row 2 key.</summary>
    Digit2,
    /// <summary>The number-row 3 key.</summary>
    Digit3,
    /// <summary>The number-row 4 key.</summary>
    Digit4,
    /// <summary>The number-row 5 key.</summary>
    Digit5,
    /// <summary>The number-row 6 key.</summary>
    Digit6,
    /// <summary>The number-row 7 key.</summary>
    Digit7,
    /// <summary>The number-row 8 key.</summary>
    Digit8,
    /// <summary>The number-row 9 key.</summary>
    Digit9,

    /// <summary>The number-row minus key.</summary>
    Minus,
    /// <summary>The number-row equals key.</summary>
    Equals,

    /// <summary>The numpad 0 key. Numpad0 through Numpad9 are contiguous.</summary>
    Numpad0,
    /// <summary>The numpad 1 key.</summary>
    Numpad1,
    /// <summary>The numpad 2 key.</summary>
    Numpad2,
    /// <summary>The numpad 3 key.</summary>
    Numpad3,
    /// <summary>The numpad 4 key.</summary>
    Numpad4,
    /// <summary>The numpad 5 key.</summary>
    Numpad5,
    /// <summary>The numpad 6 key.</summary>
    Numpad6,
    /// <summary>The numpad 7 key.</summary>
    Numpad7,
    /// <summary>The numpad 8 key.</summary>
    Numpad8,
    /// <summary>The numpad 9 key.</summary>
    Numpad9,

    /// <summary>The numpad subtract key.</summary>
    NumpadSubtract,
    /// <summary>The numpad add key.</summary>
    NumpadAdd,

    /// <summary>The F1 function key. F1 through F12 are contiguous, so <c>F1 + (n - 1)</c> indexes function key n.</summary>
    F1,

    /// <summary>The F2 function key.</summary>
    F2,

    /// <summary>The F3 function key.</summary>
    F3,

    /// <summary>The F4 function key.</summary>
    F4,

    /// <summary>The F5 function key.</summary>
    F5,

    /// <summary>The F6 function key.</summary>
    F6,

    /// <summary>The F7 function key.</summary>
    F7,

    /// <summary>The F8 function key.</summary>
    F8,

    /// <summary>The F9 function key.</summary>
    F9,

    /// <summary>The F10 function key.</summary>
    F10,

    /// <summary>The F11 function key.</summary>
    F11,

    /// <summary>The F12 function key.</summary>
    F12,

    /// <summary>The left Control key.</summary>
    ControlLeft,

    /// <summary>The right Control key.</summary>
    ControlRight,

    /// <summary>The left Shift key.</summary>
    ShiftLeft,

    /// <summary>The right Shift key.</summary>
    ShiftRight,

    /// <summary>The left Alt key.</summary>
    AltLeft,

    /// <summary>The right Alt key.</summary>
    AltRight,

    /// <summary>The left Super (Windows / Command) key.</summary>
    SuperLeft,

    /// <summary>The right Super (Windows / Command) key.</summary>
    SuperRight,
}
