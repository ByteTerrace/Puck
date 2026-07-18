namespace Puck.Input;

/// <summary>
/// Specifies a provider-neutral keyboard key. The OS-specific native windows translate their raw key codes
/// (Win32 virtual keys, X11/evdev keycodes) into these, and <see cref="WindowInputMapper"/> maps each to its
/// <see cref="InputSources.Keyboard"/> vocabulary entry — the single place a key is named. Named keys only:
/// letter keys ride <see cref="WindowInputEvent.Character"/>, and typed text rides
/// <see cref="WindowInputEvent.Text"/>.
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
}
