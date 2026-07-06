using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The shared constants of the five-star Volley cartridge: its state-machine ids, its game-owned work-RAM layout
/// (everything at <see cref="FrameworkMemoryMap.GameRam"/> and above), and the court geometry — ported verbatim from
/// the original hand-authored ROM so the physics feel is unchanged. The self-verify battery reads the SAME constants
/// it drives the ROM against, so the C# oracle and the SM83 game can never drift apart.
/// </summary>
internal static class VolleyProtocol {
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
    /// <summary>The game-over card (shown when either side reaches match point).</summary>
    public const byte StateGameOver = 5;
    /// <summary>Initials entry for a qualifying score.</summary>
    public const byte StateScoreEntry = 6;

    // Game work RAM (0xC200+).
    /// <summary>The ball's screen-space x.</summary>
    public const ushort BallX = 0xC200;
    /// <summary>The ball's screen-space y.</summary>
    public const ushort BallY = 0xC201;
    /// <summary>The ball's signed x velocity (0x02 = right, 0xFE = left).</summary>
    public const ushort BallDx = 0xC202;
    /// <summary>The ball's signed y velocity (0x02 = down, 0xFE = up).</summary>
    public const ushort BallDy = 0xC203;
    /// <summary>The top of the player's (left) paddle, screen space.</summary>
    public const ushort LeftY = 0xC204;
    /// <summary>The top of the AI's (right) paddle, screen space.</summary>
    public const ushort RightY = 0xC205;
    /// <summary>Frames left holding the ball at centre before a serve releases it.</summary>
    public const ushort ServeDelay = 0xC206;
    /// <summary>The player's match points (binary, 0..<see cref="MatchPoint"/>).</summary>
    public const ushort PlayerPoints = 0xC207;
    /// <summary>The AI's match points (binary).</summary>
    public const ushort AiPoints = 0xC208;
    /// <summary>The score: three packed-BCD bytes, most significant first (+1 per rally return, +100 per point won).</summary>
    public const ushort Score = 0xC209;
    /// <summary>The title/high-score idle counter's low byte (600 idle frames start the attract loop).</summary>
    public const ushort IdleTimer = 0xC20C;
    /// <summary>The idle counter's high byte.</summary>
    public const ushort IdleTimerHigh = 0xC20D;
    /// <summary>Frames left on the game-over card before it resolves on its own.</summary>
    public const ushort GameOverTimer = 0xC20E;
    /// <summary>The initials-entry cursor (0..2).</summary>
    public const ushort EntryCursor = 0xC20F;
    /// <summary>The three initials as letter indices (0 = A .. 25 = Z).</summary>
    public const ushort EntryGlyphs = 0xC210;
    /// <summary>Set when a point award ends the match (consumed by the play core's game-over resolution).</summary>
    public const ushort MatchOverFlag = 0xC213;

    /// <summary>The high-score table's work-RAM mirror (the framework save mirror).</summary>
    public const ushort HiScoreMirror = FrameworkMemoryMap.SaveMirror;
    /// <summary>The number of table entries.</summary>
    public const int HiScoreEntryCount = 5;
    /// <summary>Bytes per entry: three initials (font tile ids) + a three-byte BCD score, most significant first.</summary>
    public const int HiScoreEntryByteCount = 6;

    // Court geometry and tuning (verbatim from the original hand-authored ROM).
    /// <summary>Points to win the match.</summary>
    public const byte MatchPoint = 7;
    /// <summary>The ball's speed magnitude in pixels per frame.</summary>
    public const byte BallSpeed = 2;
    /// <summary>-2 as a signed velocity byte.</summary>
    public const byte NegativeSpeed = 0xFE;
    /// <summary>The player paddle's speed in pixels per frame.</summary>
    public const byte PaddleSpeed = 3;
    /// <summary>The AI paddle's chase speed in pixels per frame.</summary>
    public const byte AiSpeed = 2;
    /// <summary>The paddle height in pixels (three stacked 8-pixel sprites).</summary>
    public const byte PaddleHeight = 24;
    /// <summary>The lowest paddle-top y.</summary>
    public const byte PaddleMinY = 8;
    /// <summary>The highest paddle-top y (144 - 24 - 8).</summary>
    public const byte PaddleMaxY = 112;
    /// <summary>Where both paddles start.</summary>
    public const byte PaddleStartY = 56;
    /// <summary>The court's top wall for the ball.</summary>
    public const byte BallTopY = 8;
    /// <summary>The court's bottom wall for the ball (144 - 8 - 8).</summary>
    public const byte BallBottomY = 128;
    /// <summary>The ball x at/inside the left paddle's face.</summary>
    public const byte LeftHitX = 16;
    /// <summary>The ball x at/inside the right paddle's face.</summary>
    public const byte RightHitX = 136;
    /// <summary>Ball x at/below which the left side has missed.</summary>
    public const byte LeftMissX = 6;
    /// <summary>Ball x at/above which the right side has missed.</summary>
    public const byte RightMissX = 150;
    /// <summary>The serve position's x.</summary>
    public const byte CentreX = 76;
    /// <summary>The serve position's y.</summary>
    public const byte CentreY = 68;
    /// <summary>Frames the ball holds at centre before a serve.</summary>
    public const byte ServeFrames = 60;
}
