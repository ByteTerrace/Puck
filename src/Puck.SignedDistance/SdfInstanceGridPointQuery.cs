using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The cells a point query of the complete field evaluates, and the distance every other binned instance keeps
/// from the point. An instance is binned by its bound's center into exactly one cell (<see cref="SdfInstanceGrid"/>), so
/// an instance whose cell lies outside the block has its center past one of the block's faces that has cells beyond it,
/// and its field is at least the point's distance to that face less the largest binned radius (the grid's
/// <c>footprintPad</c>). The complete field at the point is therefore the minimum of the block's instances, the
/// always-list and <see cref="Bound"/>: exact where that minimum is below the bound, and a lower bound everywhere.
/// KEEP IN SYNC with <c>sdfGridPointBlock</c> in Assets/Shaders/Sdf/indirect/sdf-indirect-field.hlsli.</summary>
/// <param name="Min">The block's lowest cell on each axis.</param>
/// <param name="Max">The block's highest cell on each axis.</param>
/// <param name="Bound">The field every binned instance outside the block keeps from the point; positive infinity when
/// the block reaches every cell.</param>
public readonly record struct SdfInstanceGridPointBlock((int X, int Y, int Z) Min, (int X, int Y, int Z) Max, float Bound);
/// <summary>Point queries of the packed instance grid: the block of cells a query evaluates.</summary>
public static class SdfInstanceGridPointQuery {
    /// <summary>The block's half-width in cells: a point query evaluates the cells within this many of its own on each
    /// axis, a block of at most 5 by 5 by 5 cells.</summary>
    public const int BlockRadius = 2;

    /// <summary>Returns the block a point query evaluates, or false when the grid is disabled or the point lies outside
    /// its box, where a query walks every instance.</summary>
    /// <param name="block">The packed grid block (<see cref="SdfInstanceGrid"/>).</param>
    /// <param name="point">The query point.</param>
    /// <param name="result">The block and its bound.</param>
    /// <returns>Whether the grid answers the point.</returns>
    public static bool TryBlock(ReadOnlySpan<uint> block, Vector3 point, out SdfInstanceGridPointBlock result) {
        result = default;
        if ((block.Length < SdfInstanceGrid.HeaderWords) || (block[0] == 0u)) { return false; }
        var dimensions = (X: ((int)block[1]), Y: ((int)block[2]), Z: ((int)block[3]));
        var origin = new Vector3(x: Float(word: block[4]), y: Float(word: block[5]), z: Float(word: block[6]));
        var inverse = Float(word: block[7]);
        var cellSize = Float(word: block[8]);
        var pad = Float(word: block[9]);
        var local = ((point - origin) * inverse);

        if (!float.IsFinite(f: local.X) || !float.IsFinite(f: local.Y) || !float.IsFinite(f: local.Z) ||
            (local.X < 0f) || (local.Y < 0f) || (local.Z < 0f) ||
            (local.X >= dimensions.X) || (local.Y >= dimensions.Y) || (local.Z >= dimensions.Z)) { return false; }
        var cell = (X: ((int)MathF.Floor(x: local.X)), Y: ((int)MathF.Floor(x: local.Y)), Z: ((int)MathF.Floor(x: local.Z)));
        var min = (X: Math.Max(val1: 0, val2: (cell.X - BlockRadius)), Y: Math.Max(val1: 0, val2: (cell.Y - BlockRadius)), Z: Math.Max(val1: 0, val2: (cell.Z - BlockRadius)));
        var max = (X: Math.Min(val1: (dimensions.X - 1), val2: (cell.X + BlockRadius)), Y: Math.Min(val1: (dimensions.Y - 1), val2: (cell.Y + BlockRadius)),
            Z: Math.Min(val1: (dimensions.Z - 1), val2: (cell.Z + BlockRadius)));
        var reach = float.PositiveInfinity;

        // Only a face with cells beyond it can have an unvisited center past it.
        if (min.X > 0) { reach = MathF.Min(x: reach, y: (point.X - (origin.X + (min.X * cellSize)))); }
        if (min.Y > 0) { reach = MathF.Min(x: reach, y: (point.Y - (origin.Y + (min.Y * cellSize)))); }
        if (min.Z > 0) { reach = MathF.Min(x: reach, y: (point.Z - (origin.Z + (min.Z * cellSize)))); }
        if (max.X < (dimensions.X - 1)) { reach = MathF.Min(x: reach, y: ((origin.X + ((max.X + 1) * cellSize)) - point.X)); }
        if (max.Y < (dimensions.Y - 1)) { reach = MathF.Min(x: reach, y: ((origin.Y + ((max.Y + 1) * cellSize)) - point.Y)); }
        if (max.Z < (dimensions.Z - 1)) { reach = MathF.Min(x: reach, y: ((origin.Z + ((max.Z + 1) * cellSize)) - point.Z)); }
        result = new SdfInstanceGridPointBlock(Bound: (reach - pad), Max: max, Min: min);
        return true;
    }

    private static float Float(uint word) => BitConverter.UInt32BitsToSingle(value: word);
}
