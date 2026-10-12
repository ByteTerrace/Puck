using System.Collections.Concurrent;

namespace Puck.SignedDistance.Illumination;

/// <summary>
/// The GPU's ray-direction table for one ray count: the base directions (<see cref="IrradianceLattice.BaseDirection"/>)
/// followed by the continuation search's candidate lists. A query direction, taken back into a probe's base frame by its
/// orientation's inverse, falls in one of <see cref="Bins"/> squared bins of its octahedral encoding (the shaders'
/// <c>sdfOctEncode</c>, folded about Z into [-1, 1] squared); a bin lists, ascending, every base ray within
/// <see cref="IrradianceLattice.ReprojectionCone"/> of some direction in the bin. A ray outside a query's bin list is
/// outside the cone of the query, so a search over the list picks the ray a search over every ray picks.
/// <para>The table is a run of four-float elements: the ray count's directions, one element per bin holding its list's
/// first slot and length, then the lists, one ray index per float, four to an element. Every value is an integer or a
/// direction exactly representable as written.</para>
/// </summary>
public static class IrradianceDirectionTable {
    /// <summary>The bins along each side of the octahedral square.</summary>
    public const int Bins = 16;
    /// <summary>The angle, in radians, a bin's list reaches past the cone and the bin's own angular radius: it covers the
    /// single-precision rounding of the stored directions, the query and its encoding, and the sampled radius.</summary>
    public const double Margin = 1.0e-3;

    private const int Samples = 32;

    private static readonly ConcurrentDictionary<int, Table> Tables = new();

    private sealed record Table(float[] Elements, int[][] Candidates, int MaximumCandidates);

    /// <summary>Returns the table's four-float elements for a ray count, as the cache uploads them.</summary>
    /// <param name="rays">The ray count, positive.</param>
    /// <returns>The elements, flattened; a caller must not modify them.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rays"/> is not positive.</exception>
    public static ReadOnlySpan<float> Elements(int rays) => TableOf(rays: rays).Elements;
    /// <summary>Returns the base rays, ascending, a query direction in a bin can continue through.</summary>
    /// <param name="rays">The ray count, positive.</param>
    /// <param name="bin">The bin, row-major, 0 to <see cref="Bins"/> squared − 1.</param>
    /// <returns>The candidate rays.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rays"/> is not positive, or <paramref name="bin"/> is outside the table.</exception>
    public static ReadOnlySpan<int> Candidates(int rays, int bin) {
        var candidates = TableOf(rays: rays).Candidates;

        ArgumentOutOfRangeException.ThrowIfNegative(bin);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bin, candidates.Length);
        return candidates[bin];
    }
    /// <summary>Returns the longest candidate list for a ray count, which bounds one search's ray visits.</summary>
    /// <param name="rays">The ray count, positive.</param>
    /// <returns>The longest list's length.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rays"/> is not positive.</exception>
    public static int MaximumCandidates(int rays) => TableOf(rays: rays).MaximumCandidates;
    /// <summary>Returns the bin of a direction in a probe's base frame, as the shaders select it.</summary>
    /// <param name="direction">The direction; its length does not matter.</param>
    /// <returns>The bin, row-major, or −1 for a direction with no finite encoding.</returns>
    public static int BinOf(Double3 direction) {
        var (x, y) = Encode(direction: direction);

        if (!double.IsFinite(d: x) || !double.IsFinite(d: y)) { return -1; }
        var column = Math.Clamp(value: ((int)Math.Floor(d: (((x * 0.5) + 0.5) * Bins))), min: 0, max: (Bins - 1));
        var row = Math.Clamp(value: ((int)Math.Floor(d: (((y * 0.5) + 0.5) * Bins))), min: 0, max: (Bins - 1));

        return ((row * Bins) + column);
    }
    /// <summary>Takes a direction back into a probe's base frame: the inverse of <see cref="IrradianceLattice.Orient"/>.</summary>
    /// <param name="direction">The oriented direction.</param>
    /// <param name="orientation">The orientation, 0 to <see cref="IrradianceLattice.Orientations"/> − 1.</param>
    /// <returns>The base-frame direction, exactly as long as <paramref name="direction"/>.</returns>
    public static Double3 Unorient(Double3 direction, int orientation) {
        var (x, y, z) = ((orientation >> 3) switch {
            0 => (direction.X, direction.Y, direction.Z),
            1 => (direction.X, direction.Z, direction.Y),
            2 => (direction.Y, direction.X, direction.Z),
            3 => (direction.Z, direction.X, direction.Y),
            4 => (direction.Y, direction.Z, direction.X),
            _ => (direction.Z, direction.Y, direction.X),
        });

        return new Double3(X: (((orientation & 1) != 0) ? -x : x), Y: (((orientation & 2) != 0) ? -y : y), Z: (((orientation & 4) != 0) ? -z : z));
    }

    private static Table TableOf(int rays) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rays);
        return Tables.GetOrAdd(key: rays, valueFactory: static count => Build(rays: count));
    }
    private static Table Build(int rays) {
        var bases = new Double3[rays];
        var stored = new float[(rays * 4)];

        for (var ray = 0; (ray < rays); ray++) {
            bases[ray] = IrradianceLattice.BaseDirection(count: rays, index: ray);
            stored[(ray * 4)] = ((float)bases[ray].X);
            stored[((ray * 4) + 1)] = ((float)bases[ray].Y);
            stored[((ray * 4) + 2)] = ((float)bases[ray].Z);
        }
        var candidates = new int[(Bins * Bins)][];

        for (var bin = 0; (bin < candidates.Length); bin++) {
            var (center, radius) = Extent(column: (bin % Bins), row: (bin / Bins));
            var reach = Math.Cos(d: Math.Min(val1: Math.PI, val2: (((IrradianceLattice.ReprojectionCone + radius) + Margin))));

            candidates[bin] = Enumerable.Range(count: rays, start: 0).Where(predicate: ray => (Double3.Dot(a: bases[ray], b: center) >= reach)).ToArray();
        }
        var headers = (rays + candidates.Length);
        var slots = candidates.Sum(selector: static list => list.Length);
        var elements = new float[((headers + ((slots + 3) / 4)) * 4)];
        var slot = (headers * 4);

        stored.CopyTo(array: elements, index: 0);
        for (var bin = 0; (bin < candidates.Length); bin++) {
            elements[((rays + bin) * 4)] = slot;
            elements[(((rays + bin) * 4) + 1)] = candidates[bin].Length;
            foreach (var ray in candidates[bin]) { elements[slot++] = ray; }
        }
        return new Table(Candidates: candidates, Elements: elements, MaximumCandidates: candidates.Max(selector: static list => list.Length));
    }
    // A bin's centre and an angle every direction in it lies within: the largest angle from the centre to a sample of
    // the bin's square, which a closed region attains on its boundary, plus the largest angle across one sample step.
    private static (Double3 Center, double Radius) Extent(int column, int row) {
        var step = (2.0 / Bins);

        var (left, bottom) = (((column * step) - 1.0), ((row * step) - 1.0));
        var center = Decode(x: (left + (step * 0.5)), y: (bottom + (step * 0.5)));
        var farthest = 0.0;
        var across = 0.0;

        for (var i = 0; (i <= Samples); i++) {
            for (var j = 0; (j <= Samples); j++) {
                var sample = Decode(x: (left + ((step * i) / Samples)), y: (bottom + ((step * j) / Samples)));

                farthest = Math.Max(val1: farthest, val2: Angle(a: center, b: sample));
                if (i < Samples) { across = Math.Max(val1: across, val2: Angle(a: sample, b: Decode(x: (left + ((step * (i + 1)) / Samples)), y: (bottom + ((step * j) / Samples))))); }
                if (j < Samples) { across = Math.Max(val1: across, val2: Angle(a: sample, b: Decode(x: (left + ((step * i) / Samples)), y: (bottom + ((step * (j + 1)) / Samples))))); }
            }
        }
        return (center, (farthest + (2.0 * across)));
    }
    private static double Angle(Double3 a, Double3 b) => Math.Acos(d: Math.Clamp(value: Double3.Dot(a: a, b: b), min: -1.0, max: 1.0));
    // sdfOctEncode: Z folds onto the square's corners.
    private static (double X, double Y) Encode(Double3 direction) {
        var scale = (1.0 / ((Math.Abs(value: direction.X) + Math.Abs(value: direction.Y)) + Math.Abs(value: direction.Z)));

        var (x, y) = ((direction.X * scale), (direction.Y * scale));

        return ((direction.Z < 0.0) ? (((1.0 - Math.Abs(value: y)) * ((x >= 0.0) ? 1.0 : -1.0)), ((1.0 - Math.Abs(value: x)) * ((y >= 0.0) ? 1.0 : -1.0))) : (x, y));
    }
    // sdfOctDecode.
    private static Double3 Decode(double x, double y) {
        var z = (1.0 - (Math.Abs(value: x) + Math.Abs(value: y)));

        return ((z < 0.0)
            ? new Double3(X: ((1.0 - Math.Abs(value: y)) * ((x >= 0.0) ? 1.0 : -1.0)), Y: ((1.0 - Math.Abs(value: x)) * ((y >= 0.0) ? 1.0 : -1.0)), Z: z)
            : new Double3(X: x, Y: y, Z: z)).Normalize();
    }
}
