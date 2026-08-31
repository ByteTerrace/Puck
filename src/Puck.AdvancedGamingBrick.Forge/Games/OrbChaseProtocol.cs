namespace Puck.AdvancedGamingBrick.Forge.Games;

/// <summary>
/// Orb Chase's observable contract: the game-owned EWRAM fields (halfwords from <see cref="AgbForgeMemoryMap.GameRam"/>
/// up), the state ids, the 8-pixel movement/placement grid, and the mode-3 colours. Both the emitted Thumb code and
/// the verify battery address the game exclusively through these constants.
/// </summary>
public static class OrbChaseProtocol {
    /// <summary>The title state (waiting for START; the PRNG is unseeded).</summary>
    public const ushort StateTitle = 0;
    /// <summary>The play state (the player square moves; the orb scores on contact).</summary>
    public const ushort StatePlay = 1;
    /// <summary>The player square's x position in pixels (halfword; a multiple of <see cref="SquareSize"/>).</summary>
    public const uint PlayerX = (AgbForgeMemoryMap.GameRam + 0u);
    /// <summary>The player square's y position in pixels (halfword).</summary>
    public const uint PlayerY = (AgbForgeMemoryMap.GameRam + 2u);
    /// <summary>The orb's x position in pixels (halfword).</summary>
    public const uint TargetX = (AgbForgeMemoryMap.GameRam + 4u);
    /// <summary>The orb's y position in pixels (halfword).</summary>
    public const uint TargetY = (AgbForgeMemoryMap.GameRam + 6u);
    /// <summary>The score (halfword; +1 per orb reached).</summary>
    public const uint Score = (AgbForgeMemoryMap.GameRam + 8u);
    /// <summary>The byte offset of <see cref="PlayerX"/> from <see cref="AgbForgeMemoryMap.GameRam"/>.</summary>
    public const int PlayerXOffset = 0;
    /// <summary>The byte offset of <see cref="PlayerY"/>.</summary>
    public const int PlayerYOffset = 2;
    /// <summary>The byte offset of <see cref="TargetX"/>.</summary>
    public const int TargetXOffset = 4;
    /// <summary>The byte offset of <see cref="TargetY"/>.</summary>
    public const int TargetYOffset = 6;
    /// <summary>The byte offset of <see cref="Score"/>.</summary>
    public const int ScoreOffset = 8;
    /// <summary>The square/orb edge length and the movement step, in pixels.</summary>
    public const int SquareSize = 8;
    /// <summary>The placement grid's column count (x lands on 0..232 in steps of 8).</summary>
    public const int GridColumns = 30;
    /// <summary>The placement grid's row count (y lands on 0..152 in steps of 8).</summary>
    public const int GridRows = 20;
    /// <summary>The largest player/orb x (the right clamp).</summary>
    public const int MaxX = ((GridColumns - 1) * SquareSize);
    /// <summary>The largest player/orb y (the bottom clamp).</summary>
    public const int MaxY = ((GridRows - 1) * SquareSize);
    /// <summary>The player's spawn x.</summary>
    public const int PlayerStartX = 120;
    /// <summary>The player's spawn y.</summary>
    public const int PlayerStartY = 80;
    /// <summary>The backdrop colour (BGR555 black).</summary>
    public const ushort ColourBackground = 0x0000;
    /// <summary>The player square's colour (BGR555 green).</summary>
    public const ushort ColourPlayer = 0x03E0;
    /// <summary>The orb's colour (BGR555 red).</summary>
    public const ushort ColourTarget = 0x001F;
    /// <summary>The title bar's colour (BGR555 blue).</summary>
    public const ushort ColourTitleBar = 0x7C00;
    /// <summary>The title bar's height in pixels (full width, rows 0..7, drawn only in the title state).</summary>
    public const int TitleBarHeight = 8;
    /// <summary>The header title.</summary>
    public const string Title = "ORB CHASE";
    /// <summary>The header game code.</summary>
    public const string GameCode = "ORBC";
}
