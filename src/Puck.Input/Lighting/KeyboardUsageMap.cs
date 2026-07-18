namespace Puck.Input.Lighting;

/// <summary>
/// Translates a HID Keyboard/Keypad usage (usage page <c>0x07</c>) — the value a LampArray lamp declares as its
/// input binding, i.e. <em>which key it lights</em> — into Puck's provider-neutral vocabulary: a
/// <see cref="KeyCode"/> (letters and digits ride a character) and the matching
/// <see cref="InputSources.Keyboard"/> source string a binding table targets. This is the seam that lets the
/// lighting layer color a key by the command bound to it without a per-device key map.
/// </summary>
public static class KeyboardUsageMap {
    /// <summary>The HID Keyboard/Keypad usage page.</summary>
    public const ushort KeyboardUsagePage = 0x07;

    /// <summary>
    /// Resolves a HID keyboard usage to a <see cref="KeyCode"/>. Letter and digit keys resolve to
    /// <see cref="KeyCode.Letter"/> and set <paramref name="character"/> to the key's character (mirroring how the
    /// window seam carries letters on <see cref="WindowInputEvent.Character"/>); named keys resolve to their
    /// <see cref="KeyCode"/> and leave <paramref name="character"/> as <c>'\0'</c>.
    /// </summary>
    /// <param name="usage">The HID keyboard usage (page <c>0x07</c>).</param>
    /// <param name="key">When this method returns <see langword="true"/>, the resolved key.</param>
    /// <param name="character">When this method returns <see langword="true"/> for a letter/digit, its character; otherwise <c>'\0'</c>.</param>
    /// <returns><see langword="true"/> when the usage names a key in the neutral vocabulary; otherwise <see langword="false"/>.</returns>
    public static bool TryGetKeyCode(ushort usage, out KeyCode key, out char character) {
        character = '\0';

        switch (usage) {
            case >= 0x04 and <= 0x1D: // Keyboard a..z
                character = ((char)('a' + (usage - 0x04)));
                key = KeyCode.Letter;

                return true;
            case >= 0x1E and <= 0x26: // Keyboard 1..9
                character = ((char)('1' + (usage - 0x1E)));
                key = KeyCode.Letter;

                return true;
            case 0x27: // Keyboard 0
                character = '0';
                key = KeyCode.Letter;

                return true;
            case 0x28: // Keyboard Return (Enter)
                key = KeyCode.Enter;

                return true;
            case 0x29: // Keyboard Escape
                key = KeyCode.Escape;

                return true;
            case 0x2A: // Keyboard Delete (Backspace)
                key = KeyCode.Backspace;

                return true;
            case 0x2B: // Keyboard Tab
                key = KeyCode.Tab;

                return true;
            case 0x35: // Keyboard Grave Accent and Tilde (the console toggle)
                key = KeyCode.Backtick;

                return true;
            case >= 0x3A and <= 0x45: // Keyboard F1..F12
                key = ((KeyCode)(KeyCode.F1 + (usage - 0x3A)));

                return true;
            case 0x4F: // Keyboard Right Arrow
                key = KeyCode.ArrowRight;

                return true;
            case 0x50: // Keyboard Left Arrow
                key = KeyCode.ArrowLeft;

                return true;
            case 0x51: // Keyboard Down Arrow
                key = KeyCode.ArrowDown;

                return true;
            case 0x52: // Keyboard Up Arrow
                key = KeyCode.ArrowUp;

                return true;
            default:
                key = KeyCode.None;

                return false;
        }
    }

    /// <summary>
    /// Resolves a lamp's input binding (usage page + usage) to the <see cref="InputSources.Keyboard"/> source
    /// string a binding table targets. Only the keyboard page (<see cref="KeyboardUsagePage"/>) is mapped; any
    /// other page yields <see langword="false"/> (a mouse-button or unbound lamp).
    /// </summary>
    /// <param name="usagePage">The lamp's input-binding usage page.</param>
    /// <param name="usage">The lamp's input-binding usage.</param>
    /// <param name="source">When this method returns <see langword="true"/>, the neutral source string.</param>
    /// <returns><see langword="true"/> when the binding maps to a keyboard source; otherwise <see langword="false"/>.</returns>
    public static bool TryGetSource(ushort usagePage, ushort usage, out string source) {
        source = string.Empty;

        if (usagePage != KeyboardUsagePage) {
            return false;
        }

        if (!TryGetKeyCode(usage: usage, key: out var key, character: out var character)) {
            return false;
        }

        source = key switch {
            KeyCode.Letter => InputSources.Keyboard.Letter(letter: character),
            KeyCode.Enter => InputSources.Keyboard.Enter,
            KeyCode.Escape => InputSources.Keyboard.Escape,
            KeyCode.Backspace => InputSources.Keyboard.Backspace,
            KeyCode.Tab => InputSources.Keyboard.Tab,
            KeyCode.Backtick => InputSources.Keyboard.Backtick,
            KeyCode.ArrowUp => InputSources.Keyboard.ArrowUp,
            KeyCode.ArrowDown => InputSources.Keyboard.ArrowDown,
            KeyCode.ArrowLeft => InputSources.Keyboard.ArrowLeft,
            KeyCode.ArrowRight => InputSources.Keyboard.ArrowRight,
            >= KeyCode.F1 and <= KeyCode.F12 => InputSources.Keyboard.Function(number: ((key - KeyCode.F1) + 1)),
            _ => string.Empty,
        };

        return (source.Length != 0);
    }
}
