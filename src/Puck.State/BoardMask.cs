namespace Puck.State;

/// <summary>The most cells a board may hold for its occupancy to read as one 64-bit mask.</summary>
public static class BoardMask {
    /// <summary>Cell ordinals 0..63 map to bits 0..63.</summary>
    public const int MaxCells = 64;
}
