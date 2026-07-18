using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The shared constants of the ORACLE cartridge — a spare, text-only fortune-telling cart whose whole gimmick is the
/// engine's own thesis: on a deterministic machine, fortunes are always right. Its state ids, its game-owned work-RAM
/// layout (everything at <see cref="FrameworkMemoryMap.GameRam"/> and above), the screen layout, and the twelve
/// fortunes (plus the hidden thirteenth) live here as DATA. The self-verify battery reads the SAME constants and the
/// SAME fortune strings it drives the ROM against, so the C# oracle and the SM83 game can never drift apart.
/// </summary>
internal static class OracleProtocol {
    // State ids.
    /// <summary>The title screen: the word ORACLE and a blinking PRESS A TO ASK prompt.</summary>
    public const byte StateTitle = 0;
    /// <summary>The reading screen: one fortune typed out, then the ASK AGAIN prompt.</summary>
    public const byte StateReading = 1;

    // Game work RAM (0xC200+).
    /// <summary>The selected fortune (0..<see cref="FortuneCount"/>-1, or <see cref="HiddenFortuneIndex"/> for the
    /// easter egg). Picked from the frame counter at the instant the A press registers — deterministic entropy.</summary>
    public const ushort FortuneIndex = 0xC200;
    /// <summary>The typewriter's current read pointer into the selected fortune blob (low byte).</summary>
    public const ushort FortunePointerLow = 0xC201;
    /// <summary>The typewriter's read pointer (high byte).</summary>
    public const ushort FortunePointerHigh = 0xC202;
    /// <summary>Frames left before the typewriter reveals the next character.</summary>
    public const ushort TypeDelay = 0xC203;
    /// <summary>The typewriter's current map row.</summary>
    public const ushort CursorRow = 0xC204;
    /// <summary>The typewriter's current map column.</summary>
    public const ushort CursorColumn = 0xC205;
    /// <summary>Set to 1 once the fortune has finished typing (the ASK AGAIN prompt then shows and input is live).</summary>
    public const ushort DoneFlag = 0xC206;
    /// <summary>The title prompt's blink timer.</summary>
    public const ushort BlinkTimer = 0xC207;
    /// <summary>The title prompt's current visibility (1 = shown, 0 = blanked).</summary>
    public const ushort BlinkVisible = 0xC208;
    /// <summary>Set to 1 after the FIRST-EVER title tick. The easter egg (see <see cref="OracleGame"/>) only ever fires
    /// while this is 0 — power-on, frame zero — so only a scripted replay that already holds A on that first sampled
    /// frame can trigger it; a human returning to the title later can never.</summary>
    public const ushort PoweredOnce = 0xC209;

    // Layout.
    /// <summary>The A button bit of the active-high input bytes.</summary>
    public const byte ButtonA = 0x10;
    /// <summary>The B button bit.</summary>
    public const byte ButtonB = 0x20;

    /// <summary>The title word's row.</summary>
    public const int TitleRow = 4;
    /// <summary>The title word's column ("ORACLE" is 6 wide, centred on the 20-column screen).</summary>
    public const int TitleColumn = 7;
    /// <summary>The blinking prompt's row.</summary>
    public const int PromptRow = 10;
    /// <summary>The blinking prompt's column ("PRESS A TO ASK" is 14 wide, centred).</summary>
    public const int PromptColumn = 3;
    /// <summary>The reading's first text row.</summary>
    public const int ReadingBaseRow = 6;
    /// <summary>The reading's left column (also the newline carriage-return column).</summary>
    public const int ReadingBaseColumn = 1;
    /// <summary>The row step between wrapped fortune lines.</summary>
    public const int ReadingLineStep = 2;
    /// <summary>The ASK AGAIN prompt's row (below the deepest possible fortune line).</summary>
    public const int AskAgainRow = 16;
    /// <summary>The ASK AGAIN prompt's column ("ASK AGAIN" is 9 wide, centred).</summary>
    public const int AskAgainColumn = 5;

    /// <summary>The widest a wrapped fortune line may be (columns 1..18 of the 20-column screen).</summary>
    public const int MaxLineWidth = 18;
    /// <summary>The most wrapped lines a fortune may occupy (rows 6, 8, 10, 12, 14).</summary>
    public const int MaxLines = 5;
    /// <summary>Frames between typed characters — a subtle, deliberate reveal.</summary>
    public const byte TypeDelayFrames = 4;
    /// <summary>Frames per title-prompt blink phase.</summary>
    public const byte BlinkPeriod = 24;

    /// <summary>The typewriter blob's carriage-return marker (advance to the next wrapped line). Distinct from every
    /// font tile id (the font lands at bank base 0, so tile ids are 0..39) and from the terminator.</summary>
    public const byte NewlineMarker = 0xFE;
    /// <summary>The typewriter blob's end marker (the fortune is fully revealed).</summary>
    public const byte EndMarker = 0xFF;

    /// <summary>The number of ordinary fortunes indexed by the press-tick modulo.</summary>
    public const int FortuneCount = 12;
    /// <summary>The index the easter egg forces (past the modulo) — the thirteenth, hidden fortune.</summary>
    public const int HiddenFortuneIndex = 12;

    /// <summary>The word centred on the title screen.</summary>
    public const string TitleWord = "ORACLE";
    /// <summary>The blinking title prompt.</summary>
    public const string PromptText = "PRESS A TO ASK";
    /// <summary>The prompt shown once a fortune has finished typing.</summary>
    public const string AskAgainText = "ASK AGAIN";

    /// <summary>
    /// The thirteen fortunes, VERBATIM (the author's voice). Indices 0..11 are the ordinary fortunes the press-tick
    /// modulo selects; index 12 (<see cref="HiddenFortuneIndex"/>) is the hidden one the frame-perfect easter egg
    /// reveals. Every character is in the framework font's set (space, 0-9, A-Z, '-', '.').
    /// </summary>
    public static readonly string[] Fortunes = [
        "YOU WILL PRESS A AGAIN. I HAVE ALREADY SEEN IT.",
        "IN A FORKED TIMELINE YOU ALREADY WON.",
        "THE FUTURE IS FIXED. LUCKILY IT IS A GOOD ONE.",
        "REWIND ALL YOU LIKE. I SAID WHAT I SAID.",
        "YOUR FATE WAS SEALED AT POWER-ON. FRAME ZERO.",
        "SOMEWHERE A SIBLING MACHINE ASKS THE SAME QUESTION.",
        "THE ANSWER ARRIVES 42 CYCLES LATER THAN YOU THINK.",
        "TWO PATHS DIVERGE. NO THEY DO NOT. NOT IN HERE.",
        "A LINK CABLE CONNECTS YOU TO SOMEONE WHO ALREADY KNOWS.",
        "SAVE STATES CANNOT SAVE YOU. THEY CAN ONLY BRING YOU BACK.",
        "IT IS NOT LUCK. IT HAS NEVER BEEN LUCK.",
        "THE GHOST IN THIS MACHINE WROTE THIS CART. IT WISHES YOU WELL.",
        "FRAME-PERFECT. YOU MUST BE A REPLAY.",
    ];

    /// <summary>
    /// Word-wraps a fortune to <see cref="MaxLineWidth"/> and encodes it into a typewriter blob: one font tile id per
    /// character, a <see cref="NewlineMarker"/> between wrapped lines, and an <see cref="EndMarker"/> at the end. The
    /// wrap is greedy (whole words only) and validated to fit <see cref="MaxLines"/> — a fortune that would overflow
    /// the screen fails the forge at build time, never on the machine.
    /// </summary>
    /// <param name="text">The text module (its font base resolves each character to a tile id).</param>
    /// <param name="fortune">The fortune string.</param>
    /// <returns>The typewriter blob.</returns>
    public static byte[] BuildFortuneBlob(TextModule text, string fortune) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fortune);

        var lines = WrapLines(fortune: fortune);

        if (lines.Count > MaxLines) {
            throw new InvalidOperationException(message: $"The fortune \"{fortune}\" wraps to {lines.Count} lines (the reading screen holds {MaxLines}).");
        }

        var blob = new List<byte>(capacity: ((fortune.Length + lines.Count) + 1));

        for (var line = 0; (line < lines.Count); line++) {
            foreach (var character in lines[line]) {
                blob.Add(item: text.TileFor(character: character));
            }

            blob.Add(item: ((line < (lines.Count - 1)) ? NewlineMarker : EndMarker));
        }

        return [.. blob];
    }

    /// <summary>Greedily word-wraps a fortune to <see cref="MaxLineWidth"/>-column lines (the same wrap the blob and the
    /// verifier's expectations share).</summary>
    /// <param name="fortune">The fortune string.</param>
    /// <returns>The wrapped lines.</returns>
    public static IReadOnlyList<string> WrapLines(string fortune) {
        ArgumentNullException.ThrowIfNull(fortune);

        var lines = new List<string>();
        var current = "";

        foreach (var word in fortune.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries)) {
            if (word.Length > MaxLineWidth) {
                throw new InvalidOperationException(message: $"The word \"{word}\" is wider than the {MaxLineWidth}-column reading screen.");
            }

            var candidate = ((current.Length == 0) ? word : $"{current} {word}");

            if (candidate.Length > MaxLineWidth) {
                lines.Add(item: current);
                current = word;
            } else {
                current = candidate;
            }
        }

        if (current.Length > 0) {
            lines.Add(item: current);
        }

        return lines;
    }
}
