using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The CPU statement of the wallpaper fold the kernels run (<c>sdfWallpaperFoldCell</c> in
/// <c>field/sdf-point.hlsli</c>): the lattice reduction to the nearest cell, then the group's in-cell isometry keyed on
/// the cell. A program may fold only through a group whose fold is continuous (<see cref="IsContinuous"/>): such a
/// fold is built from reflections alone, so it never lengthens a distance, and the folded field never reads past the
/// nearest copy. A fold that jumps (a translation wall, a rotation seam) can read a cell's own copy while a
/// neighbour's lies nearer, which lets a march step through it.</summary>
public static class SdfWallpaperFold {
    /// <summary>The limit a hex group's lattice takes on both axes: far past any authored reach, so no cell index is
    /// ever clamped. A square group's limit is a whole number, and this value is a whole number too.</summary>
    public const float UnboundedLimit = 1000000f;
    /// <summary>The largest reciprocal cell extent a fold admits. The lattice round multiplies the fold-plane point by
    /// the reciprocal, so it bounds that product: a point as far out as 3e20 still rounds to a finite cell index, far
    /// past where a float point resolves a cell, including under an outer scale that enlarges the point a millionfold.</summary>
    public const float MaximumInverseCell = 1.0e18f;

    private const float Sqrt3 = 1.7320508f;

    private static readonly bool[] Continuity = MeasureContinuity();

    /// <summary>Whether <paramref name="group"/>'s fold is continuous everywhere in its lattice, measured at type
    /// initialization by folding pairs of points a thousandth of a cell apart across a spread of three cells each way:
    /// a continuous fold moves the pair no farther apart than they were, a jump moves it by about a cell.</summary>
    /// <param name="group">The wallpaper group.</param>
    /// <returns><see langword="true"/> when every cell wall and in-cell seam of the group is a mirror.</returns>
    public static bool IsContinuous(SdfWallpaperGroup group) => Continuity[((int)group)];
    /// <summary>Returns the reciprocal cell extents the kernels read (an instruction's Data0.zw): 1 / cell for a square
    /// lattice, and 1 / pitch and 2 / (√3 pitch) for the hex lattice, whose pitch is <paramref name="cell"/>.X. The
    /// reciprocal of the cell the fold subtracts is never floored: the lattice round and the cell displacement must
    /// read one cell, or the fold jumps at a cell boundary.</summary>
    /// <param name="group">The wallpaper group.</param>
    /// <param name="cell">The lattice cell extents.</param>
    /// <returns>The reciprocal extents.</returns>
    public static Vector2 InverseCell(SdfWallpaperGroup group, Vector2 cell) {
        var inverseX = (1f / cell.X);

        return new Vector2(
            x: inverseX,
            y: ((group >= SdfWallpaperGroup.P3)
                ? ((2f / Sqrt3) * inverseX)
                : (1f / cell.Y))
        );
    }
    /// <summary>Returns why <paramref name="cell"/> cannot be a <paramref name="group"/> fold's cell, or
    /// <see langword="null"/> when it can: the extents the group reads (X for every group, and Y for a square group, since
    /// a hex lattice is set by its pitch alone) are positive and finite, and their reciprocals (<see cref="InverseCell"/>)
    /// are finite and at most <see cref="MaximumInverseCell"/>.</summary>
    /// <param name="group">The wallpaper group.</param>
    /// <param name="cell">The lattice cell extents.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? CellRefusal(SdfWallpaperGroup group, Vector2 cell) {
        var isHex = (group >= SdfWallpaperGroup.P3);

        if (!IsPositive(value: cell.X) || (!isHex && !IsPositive(value: cell.Y))) {
            return $"the extents group {group} reads must be positive and finite; got ({cell.X}, {cell.Y})";
        }
        var inverseCell = InverseCell(cell: cell, group: group);

        if (!(inverseCell.X <= MaximumInverseCell) || !(inverseCell.Y <= MaximumInverseCell)) {
            return $"the reciprocal of a cell extent must be at most {MaximumInverseCell}, so the lattice round stays finite; got ({cell.X}, {cell.Y})";
        }

        return null;

        static bool IsPositive(float value) => (float.IsFinite(f: value) && (value > 0f));
    }
    /// <summary>Returns why <paramref name="limit"/> cannot be a <paramref name="group"/> fold's limit, or
    /// <see langword="null"/> when it can. A clamp keeps the fold continuous only where it collapses whole cells onto
    /// the boundary cell of the same lattice row: a square group's limit is a non-negative whole number of cells per
    /// axis, and a fractional one moves the clamped cell off the lattice. A hex lattice has no such clamp, since a hex
    /// edge cell has two neighbours inside, so a hex group takes <see cref="UnboundedLimit"/> on both axes.</summary>
    /// <param name="group">The wallpaper group.</param>
    /// <param name="limit">The cell-index limit per lattice axis.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? LimitRefusal(SdfWallpaperGroup group, Vector2 limit) {
        if (group >= SdfWallpaperGroup.P3) {
            return (((limit.X == UnboundedLimit) && (limit.Y == UnboundedLimit))
                ? null
                : $"a hex lattice has no continuous clamp, so group {group} takes the unbounded limit ({UnboundedLimit}) on both axes; got ({limit.X}, {limit.Y}). Bound a hex wallpaper by intersecting it with a bounding shape, not by limits");
        }

        return ((IsCount(value: limit.X) && IsCount(value: limit.Y))
            ? null
            : $"a square lattice's limit is a non-negative whole number of cells per axis, since a fractional limit clamps a cell off its lattice; got ({limit.X}, {limit.Y}). Round the limit to whole cells, or bound the wallpaper by intersecting it with a bounding shape");

        static bool IsCount(float value) => (float.IsFinite(f: value) && (value >= 0f) && (value == MathF.Floor(x: value)));
    }
    /// <summary>Folds an in-plane point onto its cell, as the kernels do.</summary>
    /// <param name="point">The point in the fold plane, in the fold's local frame.</param>
    /// <param name="group">The wallpaper group.</param>
    /// <param name="cell">The lattice cell extents (the hex pitch is X).</param>
    /// <param name="limit">The cell-index limit per square lattice axis; a hex group reads none
    /// (see <see cref="LimitRefusal"/>).</param>
    /// <param name="cellIndex">The cell the point folds in.</param>
    /// <returns>The folded point, relative to its cell's center.</returns>
    public static Vector2 Fold(Vector2 point, SdfWallpaperGroup group, Vector2 cell, Vector2 limit, out Vector2 cellIndex) {
        var inverseCell = InverseCell(cell: cell, group: group);

        if (group >= SdfWallpaperGroup.P3) {
            return FoldHex(cellIndex: out cellIndex, group: group, inversePitch: inverseCell, pitch: cell.X, q: point);
        }
        cellIndex = Vector2.Clamp(max: limit, min: -limit, value1: new Vector2(x: MathF.Round(x: (point.X * inverseCell.X)), y: MathF.Round(x: (point.Y * inverseCell.Y))));
        var r = (point - (cell * cellIndex));
        var parity = new Vector2(x: FloorMod(x: cellIndex.X, y: 2f), y: FloorMod(x: cellIndex.Y, y: 2f));

        switch (group) {
            case SdfWallpaperGroup.P4G: {
                    if (r.Y < 0f) {
                        r = ((r.X >= 0f) ? new Vector2(x: -r.Y, y: r.X) : new Vector2(x: -r.X, y: -r.Y));
                    } else if (r.X < 0f) {
                        r = new Vector2(x: r.Y, y: -r.X);
                    }
                    if ((r.X + r.Y) > (0.5f * cell.X)) {
                        r = new Vector2(x: ((0.5f * cell.X) - r.Y), y: ((0.5f * cell.X) - r.X));
                    }

                    return r;
                }
            case SdfWallpaperGroup.P4:
            case SdfWallpaperGroup.P4M: {
                    var turns = FloorMod(x: ((parity.Y - parity.X) - (2f * (parity.X * parity.Y))), y: 4f);

                    if (turns >= 2f) {
                        r = -r;
                        turns -= 2f;
                    }
                    if (turns >= 1f) {
                        r = new Vector2(x: -r.Y, y: r.X);
                    }
                    if ((group == SdfWallpaperGroup.P4M) && (r.Y > r.X)) {
                        r = new Vector2(x: r.Y, y: r.X);
                    }

                    return r;
                }
            case SdfWallpaperGroup.Pm:
                return new Vector2(x: MathF.Abs(x: r.X), y: r.Y);
            case SdfWallpaperGroup.Pmm:
                return Vector2.Abs(value: r);
        }
        var (coefU, coefV) = group switch {
            SdfWallpaperGroup.P2 => (new Vector2(x: 1f, y: 1f), new Vector2(x: 1f, y: 1f)),
            SdfWallpaperGroup.Pg => (new Vector2(x: 0f, y: 1f), Vector2.Zero),
            SdfWallpaperGroup.Cm => (new Vector2(x: 1f, y: 1f), Vector2.Zero),
            SdfWallpaperGroup.Pmg => (new Vector2(x: 1f, y: 1f), new Vector2(x: 0f, y: 1f)),
            SdfWallpaperGroup.Pgg => (new Vector2(x: 0f, y: 1f), new Vector2(x: 1f, y: 0f)),
            SdfWallpaperGroup.Cmm => (new Vector2(x: 1f, y: 0f), new Vector2(x: 0f, y: 1f)),
            _ => (Vector2.Zero, Vector2.Zero),
        };
        var folded = (r * new Vector2(
            x: (1f - (2f * FloorMod(x: Vector2.Dot(value1: coefU, value2: parity), y: 2f))),
            y: (1f - (2f * FloorMod(x: Vector2.Dot(value1: coefV, value2: parity), y: 2f)))
        ));

        return (((group == SdfWallpaperGroup.Cmm) && (folded.Y < 0f)) ? -folded : folded);
    }

    private static Vector2 FoldHex(Vector2 q, SdfWallpaperGroup group, float pitch, Vector2 inversePitch, out Vector2 cellIndex) {
        var axialB = (q.Y * inversePitch.Y);
        var axialA = ((q.X * inversePitch.X) - (0.5f * axialB));
        var axialC = -(axialA + axialB);
        var roundedA = MathF.Round(x: axialA);
        var roundedB = MathF.Round(x: axialB);
        var roundedC = MathF.Round(x: axialC);
        var errorA = MathF.Abs(x: (roundedA - axialA));
        var errorB = MathF.Abs(x: (roundedB - axialB));
        var errorC = MathF.Abs(x: (roundedC - axialC));

        if ((errorA > errorB) && (errorA > errorC)) {
            roundedA = -(roundedB + roundedC);
        } else if (errorB > errorC) {
            roundedB = -(roundedA + roundedC);
        }
        cellIndex = new Vector2(x: roundedA, y: roundedB);
        var r = (q - (new Vector2(x: (roundedA + (0.5f * roundedB)), y: (roundedB * (Sqrt3 * 0.5f))) * pitch));

        if (group == SdfWallpaperGroup.P6) {
            var sector = MathF.Floor(x: (MathF.Atan2(x: r.X, y: r.Y) * (3f / MathF.PI)));
            var spin = -(sector * (MathF.PI / 3f));

            var (sin, cos) = MathF.SinCos(x: spin);

            return new Vector2(x: ((cos * r.X) - (sin * r.Y)), y: ((sin * r.X) + (cos * r.Y)));
        }
        if (group == SdfWallpaperGroup.P3) {
            var turns = FloorMod(x: (roundedA - roundedB), y: 3f);

            for (var turn = 1f; (turn <= turns); turn++) {
                r = new Vector2(x: ((-0.5f * r.X) - ((Sqrt3 * 0.5f) * r.Y)), y: (((Sqrt3 * 0.5f) * r.X) - (0.5f * r.Y)));
            }

            return r;
        }
        var cornerMirrors = (group is SdfWallpaperGroup.P3M1 or SdfWallpaperGroup.P6M);
        var edgeMirrors = (group is SdfWallpaperGroup.P31M or SdfWallpaperGroup.P6M);

        if (edgeMirrors && (r.Y < 0f)) {
            r.Y = -r.Y;
        }
        if (cornerMirrors && (r.X < 0f)) {
            r.X = -r.X;
        }
        if (edgeMirrors && (Vector2.Dot(value1: r, value2: new Vector2(x: -(Sqrt3 * 0.5f), y: 0.5f)) > 0f)) {
            r = new Vector2(x: ((-0.5f * r.X) + ((Sqrt3 * 0.5f) * r.Y)), y: (((Sqrt3 * 0.5f) * r.X) + (0.5f * r.Y)));
        }
        if (edgeMirrors && (r.Y < 0f)) {
            r.Y = -r.Y;
        }
        if (cornerMirrors && (Vector2.Dot(value1: r, value2: new Vector2(x: -0.5f, y: (Sqrt3 * 0.5f))) < 0f)) {
            r = new Vector2(x: ((0.5f * r.X) + ((Sqrt3 * 0.5f) * r.Y)), y: (((Sqrt3 * 0.5f) * r.X) - (0.5f * r.Y)));
        }
        if (cornerMirrors && (r.X < 0f)) {
            r.X = -r.X;
        }

        return r;
    }
    private static float FloorMod(float x, float y) => (x - (y * MathF.Floor(x: (x / y))));
    // Unit cells, an unbounded lattice, and pairs a thousandth apart along the R2 sequence's points and directions:
    // a reflection keeps the pair's distance, a jump multiplies it by about a thousand.
    private static bool[] MeasureContinuity() {
        var groups = Enum.GetValues<SdfWallpaperGroup>();
        var continuity = new bool[groups.Length];
        var cell = Vector2.One;
        var limit = new Vector2(value: UnboundedLimit);

        foreach (var group in groups) {
            var stretch = 0f;

            for (var index = 1; (index <= 65536); index++) {
                var point = new Vector2(x: ((Fraction(value: (index * 0.7548777f)) * 6f) - 3f), y: ((Fraction(value: (index * 0.5698403f)) * 6f) - 3f));
                var angle = (Fraction(value: (index * 0.6180340f)) * (2f * MathF.PI));
                var step = (0.001f * new Vector2(x: MathF.Cos(x: angle), y: MathF.Sin(x: angle)));
                var a = Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: point);
                var b = Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: (point + step));

                stretch = MathF.Max(x: stretch, y: (Vector2.Distance(value1: a, value2: b) / step.Length()));
            }
            continuity[((int)group)] = (stretch < 1.01f);
        }

        return continuity;

        static float Fraction(float value) => (value - MathF.Floor(x: value));
    }
}
