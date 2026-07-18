namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// A deterministic per-machine joypad script: an ordered set of keyframes, each holding a button state from its frame
/// until the next keyframe supersedes it. The script is a pure function of frame index — no wall clock, no RNG — so a
/// linked run driven by two of these is replay-identical, exactly the property the cross-generation link-game gate
/// (<see cref="LinkGameReplayStage"/>) rests on. The demo's diegetic <c>JoypadSegment</c> pattern is the same idea;
/// here the segments are captured once (by exploring the game interactively with <see cref="LinkExplore"/>) and then
/// frozen into the gate.
/// </summary>
internal sealed class LinkInputScript {
    private readonly (int Frame, JoypadButtons Buttons)[] m_keyframes;

    /// <summary>Creates a script from keyframes given in ascending frame order. A leading keyframe at frame 0 is
    /// optional; before the first keyframe the held state is <see cref="JoypadButtons.None"/>.</summary>
    /// <param name="keyframes">The keyframes, each a (frame, held-buttons) pair, in strictly ascending frame order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keyframes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The keyframes are not in strictly ascending frame order.</exception>
    public LinkInputScript(params (int Frame, JoypadButtons Buttons)[] keyframes) {
        ArgumentNullException.ThrowIfNull(argument: keyframes);

        for (var index = 1; (index < keyframes.Length); ++index) {
            if (keyframes[index].Frame <= keyframes[(index - 1)].Frame) {
                throw new ArgumentException(message: "link-script keyframes must be in strictly ascending frame order.", paramName: nameof(keyframes));
            }
        }

        m_keyframes = keyframes;
    }

    /// <summary>The held button state at a frame: the buttons of the latest keyframe whose frame is at or before
    /// <paramref name="frame"/>, or <see cref="JoypadButtons.None"/> before the first keyframe.</summary>
    /// <param name="frame">The frame index to query.</param>
    /// <returns>The held buttons for that frame.</returns>
    public JoypadButtons ButtonsAt(int frame) {
        var held = JoypadButtons.None;

        foreach (var (keyframe, buttons) in m_keyframes) {
            if (keyframe > frame) {
                break;
            }

            held = buttons;
        }

        return held;
    }

    /// <summary>Parses a text script — one <c>frame&#160;BUTTONS</c> keyframe per non-empty, non-<c>#</c>-comment line,
    /// where BUTTONS is <c>None</c> or a <c>+</c>/<c>,</c>-separated list of button names (Right, Left, Up, Down, A, B,
    /// Select, Start; case-insensitive). Used by the interactive explorer; the gate itself hard-codes its frozen
    /// scripts rather than reading files.</summary>
    /// <param name="path">The script file path.</param>
    /// <returns>The parsed script.</returns>
    /// <exception cref="FormatException">A line is malformed.</exception>
    public static LinkInputScript Load(string path) {
        var keyframes = new List<(int Frame, JoypadButtons Buttons)>();

        foreach (var raw in File.ReadAllLines(path: path)) {
            var line = raw.Trim();

            if ((line.Length == 0) || line.StartsWith(value: '#')) {
                continue;
            }

            var space = line.IndexOf(value: ' ');
            var frameToken = ((space < 0) ? line : line[..space]);
            var buttonToken = ((space < 0) ? "None" : line[(space + 1)..].Trim());

            if (!int.TryParse(s: frameToken, result: out var frame)) {
                throw new FormatException(message: $"link-script line '{raw}' does not start with a frame number");
            }

            keyframes.Add(item: (frame, ParseButtons(text: buttonToken)));
        }

        return new LinkInputScript(keyframes: [.. keyframes]);
    }

    /// <summary>Parses a button token: <c>None</c> or a <c>+</c>/<c>,</c>-separated list of button names.</summary>
    /// <param name="text">The token to parse.</param>
    /// <returns>The combined button flags.</returns>
    /// <exception cref="FormatException">A name is not a known button.</exception>
    public static JoypadButtons ParseButtons(string text) {
        var buttons = JoypadButtons.None;

        foreach (var name in text.Split(separator: ['+', ','], options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            buttons |= name.ToLowerInvariant() switch {
                "none" => JoypadButtons.None,
                "right" => JoypadButtons.Right,
                "left" => JoypadButtons.Left,
                "up" => JoypadButtons.Up,
                "down" => JoypadButtons.Down,
                "a" => JoypadButtons.A,
                "b" => JoypadButtons.B,
                "select" => JoypadButtons.Select,
                "start" => JoypadButtons.Start,
                _ => throw new FormatException(message: $"'{name}' is not a known button"),
            };
        }

        return buttons;
    }
}
