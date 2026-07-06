using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The shared constants of the five-star Chroma cartridge: its state-machine ids, its game-owned work-RAM layout
/// (everything at <see cref="FrameworkMemoryMap.GameRam"/> and above), and the well geometry — ported verbatim from
/// the original hand-authored ROM so the drip/swap/cascade feel is unchanged. The self-verify battery reads the SAME
/// constants it drives the ROM against, so the C# oracle and the SM83 game can never drift apart.
/// </summary>
internal static class ChromaProtocol {
    // State ids.
    /// <summary>The title screen.</summary>
    public const byte StateTitle = 0;
    /// <summary>The scripted attract loop (a real play tick driven by a baked input script).</summary>
    public const byte StateAttract = 1;
    /// <summary>The battery-backed high-score table screen.</summary>
    public const byte StateHighScores = 2;
    /// <summary>Live play.</summary>
    public const byte StatePlay = 3;
    /// <summary>Paused (the simulation halts by construction — the play tick simply never runs).</summary>
    public const byte StatePause = 4;
    /// <summary>The game-over card (shown when a drip tops the well out).</summary>
    public const byte StateGameOver = 5;
    /// <summary>Initials entry for a qualifying score.</summary>
    public const byte StateScoreEntry = 6;

    // Game work RAM (0xC200+).
    /// <summary>The cursor's well column (0..5).</summary>
    public const ushort CursorCol = 0xC200;
    /// <summary>The cursor's well row (0..11).</summary>
    public const ushort CursorRow = 0xC201;
    /// <summary>Frames since the last drip (a new block spawns at <see cref="DropInterval"/>).</summary>
    public const ushort DropTimer = 0xC202;
    /// <summary>The score: three packed-BCD bytes, most significant first (+1 per cleared block).</summary>
    public const ushort Score = 0xC203;
    /// <summary>Whether the last scan/apply pass cleared anything (drives the cascade loop).</summary>
    public const ushort AnyMarked = 0xC206;
    /// <summary>Address-math scratch: the current linear cell index.</summary>
    public const ushort Idx = 0xC207;
    /// <summary>Address-math scratch: the current row.</summary>
    public const ushort Rr = 0xC208;
    /// <summary>Address-math scratch: the current column.</summary>
    public const ushort Cc = 0xC209;
    /// <summary>The gravity pass's per-column gather count.</summary>
    public const ushort Ki = 0xC20A;
    /// <summary>The pending spawn's column.</summary>
    public const ushort SpawnCol = 0xC20B;
    /// <summary>The pending spawn's colour (1..3).</summary>
    public const ushort SpawnColour = 0xC20C;
    /// <summary>The cascade loop's iteration backstop.</summary>
    public const ushort ResolveGuard = 0xC20D;
    /// <summary>Set when a drip finds its column's top cell occupied (consumed by the play core's resolution).</summary>
    public const ushort TopOutFlag = 0xC20E;
    /// <summary>The title/high-score idle counter's low byte (600 idle frames start the attract loop).</summary>
    public const ushort IdleTimer = 0xC20F;
    /// <summary>The idle counter's high byte.</summary>
    public const ushort IdleTimerHigh = 0xC210;
    /// <summary>Frames left on the game-over card before it resolves on its own.</summary>
    public const ushort GameOverTimer = 0xC211;
    /// <summary>The initials-entry cursor (0..2).</summary>
    public const ushort EntryCursor = 0xC212;
    /// <summary>The three initials as letter indices (0 = A .. 25 = Z).</summary>
    public const ushort EntryGlyphs = 0xC213;
    /// <summary>The screen-diff pass's per-frame push budget scratch.</summary>
    public const ushort DiffBudget = 0xC216;
    /// <summary>Set by the apply pass whenever a resolve cleared at least one block (consumed by the play core's
    /// sweep-effect trigger).</summary>
    public const ushort ClearedFlag = 0xC217;

    /// <summary>The well: 6 × 12 = 72 cells, row-major (0 = empty, 1..3 = colour, which IS the block's tile id).</summary>
    public const ushort GridBase = 0xC220;
    /// <summary>The cascade scan's 72 clear flags.</summary>
    public const ushort MarkBase = 0xC270;
    /// <summary>The gravity pass's 12-byte per-column gather scratch.</summary>
    public const ushort TempBase = 0xC2C0;
    /// <summary>The on-screen shadow of the well (what the background map currently shows) — the per-frame diff pass
    /// queues only the cells where the grid and this shadow disagree, so cascades repaint without an LCD-off flash.</summary>
    public const ushort ScreenBase = 0xC2D0;

    /// <summary>The high-score table's work-RAM mirror (the framework save mirror).</summary>
    public const ushort HiScoreMirror = FrameworkMemoryMap.SaveMirror;
    /// <summary>The number of table entries.</summary>
    public const int HiScoreEntryCount = 5;
    /// <summary>Bytes per entry: three initials (font tile ids) + a three-byte BCD score, most significant first.</summary>
    public const int HiScoreEntryByteCount = 6;

    // Well geometry and tuning (verbatim from the original hand-authored ROM).
    /// <summary>The well width in cells.</summary>
    public const int Cols = 6;
    /// <summary>The well height in cells.</summary>
    public const int Rows = 12;
    /// <summary>Frames between drips.</summary>
    public const byte DropInterval = 24;
    /// <summary>The well's top-left screen cell: map row 3, column 7.</summary>
    public const int WellScreenRow = 3;
    /// <summary>The well's left screen column.</summary>
    public const int WellScreenColumn = 7;
    /// <summary>How many blocks the fresh-game seeding drips into the well.</summary>
    public const int SeedDrips = 12;
}
