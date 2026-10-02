using Puck.SignedDistance.Baking;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The CPU oracle of the impostor card's ray trace (<c>frame/sdf-mesh-impostor.hlsli</c>, <c>sdfImpostorTrace</c>), written
/// in doubles over the decoded depth atlas at level zero: the three views nearest the direction toward the camera, each
/// marched through the sphere against its depth, and the weighted mean of the hits the views agree on. KEEP IN SYNC with
/// that function: the same octahedral encode and triangle of views, the same view basis, the same texel layout, the same
/// eight steps with a linear refinement, the same miss depth and the same majority rule.
/// </summary>
internal sealed class SdfImpostorOracle(SdfBakedImpostor impostor) {
    internal const double MissDepth = 0.98;
    internal const double MissGap = 16.0;
    internal const int Steps = 8;

    private readonly byte[] m_albedo = impostor.Albedo.Decode(level: 0);
    private readonly byte[] m_depth = impostor.Depth.Decode(level: 0);
    private readonly byte[] m_material = impostor.Material.Levels[0];

    // The octahedral map of a direction onto [-1, 1]^2, +Y the pole.
    internal static (double X, double Z) Encode((double X, double Y, double Z) v) {
        var norm = ((Math.Abs(value: v.X) + Math.Abs(value: v.Y)) + Math.Abs(value: v.Z));

        var (x, z) = ((v.X / norm), (v.Z / norm));

        if (v.Y < 0.0) {
            (x, z) = (((1.0 - Math.Abs(value: z)) * ((x >= 0.0) ? 1.0 : -1.0)), ((1.0 - Math.Abs(value: x)) * ((z >= 0.0) ? 1.0 : -1.0)));
        }

        return (x, z);
    }
    // The three views nearest a direction, as a triangle of the view grid, with their weights.
    internal static ((int I, int J) A, (int I, int J) B, (int I, int J) C, double Wa, double Wb, double Wc) Views(int views, (double X, double Y, double Z) toward) {
        var (px, pz) = Encode(v: toward);
        var gx = ((((px * 0.5) + 0.5) * views) - 0.5);
        var gz = ((((pz * 0.5) + 0.5) * views) - 0.5);

        var (bx, bz) = (Math.Floor(d: gx), Math.Floor(d: gz));
        var (fx, fz) = ((gx - bx), (gz - bz));
        (int I, int J) Wrap(double x, double y) {
            var (i, j) = (((int)x), ((int)y));
            if ((i < 0) || (i >= views)) {
                i = ((i < 0) ? (-i - 1) : (((2 * views) - i) - 1));
                j = ((views - j) - 1);
            }
            if ((j < 0) || (j >= views)) {
                j = ((j < 0) ? (-j - 1) : (((2 * views) - j) - 1));
                i = ((views - i) - 1);
            }
            return (i, j);
        }

        return (((fx + fz) < 1.0)
            ? (Wrap(x: bx, y: bz), Wrap(x: (bx + 1.0), y: bz), Wrap(x: bx, y: (bz + 1.0)), ((1.0 - fx) - fz), fx, fz)
            : (Wrap(x: (bx + 1.0), y: (bz + 1.0)), Wrap(x: (bx + 1.0), y: bz), Wrap(x: bx, y: (bz + 1.0)), ((fx + fz) - 1.0), (1.0 - fz), (1.0 - fx)));
    }
    /// <summary>Marches a ray through the impostor's views.</summary>
    /// <param name="start">The ray's point at parameter zero, in the sphere's unit coordinates (center zero, radius one).</param>
    /// <param name="travel">The ray's change per unit parameter, in the same coordinates.</param>
    /// <param name="swapBasis">Mutates the view basis: right and up exchanged, which the law proves the oracle refuses.</param>
    /// <param name="t">The parameter of the hit.</param>
    /// <returns><see langword="true"/> when the views agree on a hit.</returns>
    internal bool Trace((double X, double Y, double Z) start, (double X, double Y, double Z) travel, bool swapBasis, out double t) {
        t = 0.0;

        var a = Dot(a: travel, b: travel);
        var b = Dot(a: start, b: travel);
        var c = (Dot(a: start, b: start) - 1.0);
        var discriminant = ((b * b) - (a * c));

        if (discriminant <= 0.0) {
            return false;
        }

        var root = Math.Sqrt(d: discriminant);
        var leave = ((root - b) / a);
        var entry = Math.Max(val1: ((-b - root) / a), val2: 0.0);

        if (leave <= entry) {
            return false;
        }

        var length = Math.Sqrt(d: a);
        var chosen = Views(toward: ((-travel.X / length), (-travel.Y / length), (-travel.Z / length)), views: impostor.Views);
        var covered = 0.0;
        var sum = 0.0;

        foreach (var (cell, weight) in new[] { (chosen.A, chosen.Wa), (chosen.B, chosen.Wb), (chosen.C, chosen.Wc) }) {
            if (ViewHit(cell: cell, entry: entry, hit: out var hit, leave: leave, start: start, swapBasis: swapBasis, travel: travel)) {
                covered += weight;
                sum += (weight * hit);
            }
        }

        if (covered < 0.5) {
            return false;
        }

        t = (sum / covered);

        return true;
    }
    /// <summary>Marches a ray and names the material of the surface it meets: the texel the most-weighted covering view
    /// holds at the hit, as <c>sdfImpostorSurfaceAt</c> chooses it.</summary>
    /// <param name="start">The ray's point at parameter zero, in the sphere's unit coordinates.</param>
    /// <param name="travel">The ray's change per unit parameter.</param>
    /// <param name="firstMaterialOnly">Mutates the read to the first-material behaviour the material plane replaced: every
    /// pixel of a card names material zero.</param>
    /// <param name="t">The parameter of the hit.</param>
    /// <param name="material">The material entry.</param>
    /// <returns><see langword="true"/> when the views agree on a hit.</returns>
    internal bool TraceMaterial((double X, double Y, double Z) start, (double X, double Y, double Z) travel, bool firstMaterialOnly, out double t, out int material) {
        material = 0;

        if (!Trace(start: start, swapBasis: false, t: out t, travel: travel)) {
            return false;
        }

        if (firstMaterialOnly) {
            return true;
        }

        var length = Math.Sqrt(d: Dot(a: travel, b: travel));
        var chosen = Views(toward: ((-travel.X / length), (-travel.Y / length), (-travel.Z / length)), views: impostor.Views);
        var spot = ((start.X + (travel.X * t)), (start.Y + (travel.Y * t)), (start.Z + (travel.Z * t)));
        var heaviest = -1.0;

        foreach (var (cell, weight) in new[] { (chosen.A, chosen.Wa), (chosen.B, chosen.Wb), (chosen.C, chosen.Wc) }) {
            var toward = SdfBakedImpostor.ViewDirection(i: cell.I, j: cell.J, views: impostor.Views);
            var v = (((double)toward.X), ((double)toward.Y), ((double)toward.Z));
            var reference = ((Math.Abs(value: v.Item2) > 0.999) ? (0.0, 0.0, 1.0) : (0.0, 1.0, 0.0));
            var right = Normalize(v: Cross(a: reference, b: v));
            var up = Cross(a: v, b: right);
            var tile = impostor.ViewTexels;
            var ix = Math.Clamp(value: ((int)Math.Floor(d: (((Dot(a: spot, b: right) * 0.5) + 0.5) * tile))), min: 0, max: (tile - 1));
            var iy = Math.Clamp(value: ((int)Math.Floor(d: ((0.5 - (Dot(a: spot, b: up) * 0.5)) * tile))), min: 0, max: (tile - 1));
            var at = ((((cell.J * tile) + iy) * (impostor.Views * tile)) + ((cell.I * tile) + ix));
            var covered = (weight * (m_albedo[((at * 4) + 3)] / 255.0));

            if (covered > heaviest) {
                heaviest = covered;
                material = m_material[at];
            }
        }

        return true;
    }

    private bool ViewHit((int I, int J) cell, double entry, double leave, (double X, double Y, double Z) start, (double X, double Y, double Z) travel, bool swapBasis, out double hit) {
        var toward = SdfBakedImpostor.ViewDirection(i: cell.I, j: cell.J, views: impostor.Views);
        var v = (((double)toward.X), ((double)toward.Y), ((double)toward.Z));
        var reference = ((Math.Abs(value: v.Item2) > 0.999) ? (0.0, 0.0, 1.0) : (0.0, 1.0, 0.0));
        var right = Normalize(v: Cross(a: reference, b: v));
        var up = Cross(a: v, b: right);

        if (swapBasis) {
            (right, up) = (up, right);
        }

        hit = entry;

        var previousGap = MissGap;
        var previousAt = entry;

        for (var n = 0; (n <= Steps); n++) {
            var at = (entry + ((leave - entry) * (((double)n) / Steps)));
            var spot = ((start.X + (travel.X * at)), (start.Y + (travel.Y * at)), (start.Z + (travel.Z * at)));
            var depth = DepthAt(a: Dot(a: spot, b: right), b: Dot(a: spot, b: up), cell: cell);
            var gap = ((depth < MissDepth) ? (Dot(a: spot, b: v) - (1.0 - (2.0 * depth))) : MissGap);

            if (gap <= 0.0) {
                hit = ((n == 0) ? at : (previousAt + ((at - previousAt) * (previousGap / (previousGap - gap)))));

                return true;
            }

            previousGap = gap;
            previousAt = at;
        }

        return false;
    }
    private double DepthAt(double a, double b, (int I, int J) cell) {
        var tile = impostor.ViewTexels;
        var ix = Math.Clamp(value: ((int)Math.Floor(d: (((a * 0.5) + 0.5) * tile))), min: 0, max: (tile - 1));
        var iy = Math.Clamp(value: ((int)Math.Floor(d: ((0.5 - (b * 0.5)) * tile))), min: 0, max: (tile - 1));
        var side = (impostor.Views * tile);

        return (m_depth[((((cell.J * tile) + iy) * side) + ((cell.I * tile) + ix))] / 255.0);
    }
    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z));
    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (((a.Y * b.Z) - (a.Z * b.Y)), ((a.Z * b.X) - (a.X * b.Z)), ((a.X * b.Y) - (a.Y * b.X)));
    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v) {
        var length = Math.Sqrt(d: Dot(a: v, b: v));

        return ((v.X / length), (v.Y / length), (v.Z / length));
    }
}
