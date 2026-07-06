using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The shared constants of the five-star Brickfall cartridge: its state-machine ids, its game-owned work-RAM layout
/// (everything at <see cref="FrameworkMemoryMap.GameRam"/> and above), and the well geometry. The self-verify battery
/// reads the SAME constants it drives the ROM against, so the C# oracle and the SM83 game can never drift apart.
/// </summary>
internal static class BrickfallProtocol {
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
    /// <summary>The game-over card.</summary>
    public const byte StateGameOver = 5;
    /// <summary>Initials entry for a qualifying score.</summary>
    public const byte StateScoreEntry = 6;

    // Game work RAM (0xC200+).
    /// <summary>The falling piece's type (0..6, the I/O/T/S/Z/J/L order of the piece table).</summary>
    public const ushort PieceType = 0xC200;
    /// <summary>The falling piece's rotation (0..3).</summary>
    public const ushort PieceRot = 0xC201;
    /// <summary>The falling piece's well column.</summary>
    public const ushort PieceX = 0xC202;
    /// <summary>The falling piece's well row.</summary>
    public const ushort PieceY = 0xC203;
    /// <summary>The preview piece's type (promoted to the falling piece on spawn).</summary>
    public const ushort NextPiece = 0xC204;
    /// <summary>The gravity timer (frames since the last row drop).</summary>
    public const ushort DropTimer = 0xC205;
    /// <summary>The score: three packed-BCD bytes, most significant first (six digits).</summary>
    public const ushort Score = 0xC206;
    /// <summary>The cleared-line count: two packed-BCD bytes, most significant first (four digits).</summary>
    public const ushort Lines = 0xC209;
    /// <summary>The level as one packed-BCD byte (two digits, for display).</summary>
    public const ushort LevelBcd = 0xC20B;
    /// <summary>The level as a plain binary byte (indexes the speed table).</summary>
    public const ushort LevelBin = 0xC20C;
    /// <summary>Lines toward the next level (binary 0..9; ten lines per level).</summary>
    public const ushort LinesUnits = 0xC20D;
    /// <summary>The collision probe's candidate column.</summary>
    public const ushort TestX = 0xC20E;
    /// <summary>The collision probe's candidate row.</summary>
    public const ushort TestY = 0xC20F;
    /// <summary>The collision probe's candidate rotation.</summary>
    public const ushort TestRot = 0xC210;
    /// <summary>The collision probe's result (1 = collides).</summary>
    public const ushort CollideFlag = 0xC211;
    /// <summary>The title/high-score idle counter's low byte (600 idle frames start the attract loop).</summary>
    public const ushort IdleTimer = 0xC212;
    /// <summary>The idle counter's high byte.</summary>
    public const ushort IdleTimerHigh = 0xC213;
    /// <summary>Frames left on the game-over card before it resolves on its own.</summary>
    public const ushort GameOverTimer = 0xC214;
    /// <summary>The initials-entry cursor (0..2).</summary>
    public const ushort EntryCursor = 0xC215;
    /// <summary>The three initials as letter indices (0 = A .. 25 = Z).</summary>
    public const ushort EntryGlyphs = 0xC216;
    /// <summary>Rows cleared by the last lock (1..4; scratch for the award pass).</summary>
    public const ushort ClearedCount = 0xC219;
    /// <summary>The clear scan's current row (scratch).</summary>
    public const ushort RowScan = 0xC21A;
    /// <summary>Address-math scratch: the probed column.</summary>
    public const ushort ColT = 0xC21B;
    /// <summary>Address-math scratch: the probed row.</summary>
    public const ushort RowT = 0xC21C;
    /// <summary>The shift-down pass's current row (scratch).</summary>
    public const ushort ShiftRow = 0xC21D;
    /// <summary>The falling piece's block tile id (colour cycles with the piece type).</summary>
    public const ushort BlockTile = 0xC21E;
    /// <summary>Set while the pending drop is a soft drop (scores +1 per row).</summary>
    public const ushort SoftDropFlag = 0xC21F;
    /// <summary>The well shadow: 10 × 18 = 180 tile ids, row-major (0 = empty).</summary>
    public const ushort WellBase = 0xC220;

    /// <summary>The high-score table's work-RAM mirror (the framework save mirror).</summary>
    public const ushort HiScoreMirror = FrameworkMemoryMap.SaveMirror;
    /// <summary>The number of table entries.</summary>
    public const int HiScoreEntryCount = 5;
    /// <summary>Bytes per entry: three initials (font tile ids) + a three-byte BCD score, most significant first.</summary>
    public const int HiScoreEntryByteCount = 6;

    /// <summary>The well width in cells.</summary>
    public const int WellColumns = 10;
    /// <summary>The well height in cells.</summary>
    public const int WellRows = 18;
}
