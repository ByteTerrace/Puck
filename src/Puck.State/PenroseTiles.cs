namespace Puck.State;

/// <summary>Which of the two Penrose P3 rhombs a tile is.</summary>
public enum PenroseRhomb : byte {
    /// <summary>The 36°/144° rhomb.</summary>
    Thin,

    /// <summary>The 72°/108° rhomb.</summary>
    Fat,
}
/// <summary>A Penrose tiling's per-tile geometry, in the cell order <see cref="TilingGenerator.Generate"/> lays
/// down: which rhomb each tile is, and the tiles that share a side with it.</summary>
/// <param name="Kinds">One rhomb kind per cell ordinal.</param>
/// <param name="Sides">Four entries per cell ordinal, in the tile's own vertex order: the cell ordinal sharing that
/// side, or <c>-1</c> when nothing does. Sides 0 and 2 are one parallel pair, sides 1 and 3 the other.</param>
public sealed record PenroseTiles(IReadOnlyList<PenroseRhomb> Kinds, IReadOnlyList<IReadOnlyList<int>> Sides);
