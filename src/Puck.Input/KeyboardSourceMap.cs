namespace Puck.Input;

/// <summary>
/// Maps a provider-neutral keyboard key to the canonical <see cref="InputSources.Keyboard"/> source id. This is
/// the one key-to-vocabulary table shared by live window input and keyboard-lighting usage maps.
/// </summary>
internal static class KeyboardSourceMap {
    /// <summary>Attempts to resolve a neutral key and its optional character payload to a source id.</summary>
    public static bool TryGetSource(KeyCode key, char character, out string source) {
        source = key switch {
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
            KeyCode.Letter when char.IsAsciiLetter(c: character) => InputSources.Keyboard.Letter(letter: character),
            >= KeyCode.F1 and <= KeyCode.F12 => InputSources.Keyboard.Function(number: ((key - KeyCode.F1) + 1)),
            KeyCode.ControlLeft => InputSources.Keyboard.ControlLeft,
            KeyCode.ControlRight => InputSources.Keyboard.ControlRight,
            KeyCode.ShiftLeft => InputSources.Keyboard.ShiftLeft,
            KeyCode.ShiftRight => InputSources.Keyboard.ShiftRight,
            KeyCode.AltLeft => InputSources.Keyboard.AltLeft,
            KeyCode.AltRight => InputSources.Keyboard.AltRight,
            KeyCode.SuperLeft => InputSources.Keyboard.SuperLeft,
            KeyCode.SuperRight => InputSources.Keyboard.SuperRight,
            _ => string.Empty,
        };

        return (source.Length != 0);
    }
}
