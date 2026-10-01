namespace Puck.World.Tests.FidgetStudy;

/// <summary>One screen tile's pruning outcome.</summary>
/// <param name="Visible">The instructions the tile's instance mask leaves (world segments plus visible instances).</param>
/// <param name="TileLive">The instructions live anywhere in the tile's marched depth range (one tape per tile).</param>
/// <param name="SlabLiveMean">The mean live instructions per depth slab (one tape per tile and depth slab).</param>
/// <param name="Entered">Whether any surface can lie in the tile (a sky tile walks no tape at all).</param>
/// <param name="Slabs">The depth slabs between the tile's entry and its exit.</param>
public readonly record struct SdfStudyTileResult(int Visible, int TileLive, double SlabLiveMean, bool Entered, int Slabs);
/// <summary>Counted march work for one configuration of the per-sample walk.</summary>
public sealed class SdfStudyMarchTotals {
    /// <summary>The configurations: the unmasked program, the instance mask (today's walk), the tile tape, the
    /// depth-slab tape, and the tile tape without the per-sample sphere skips.</summary>
    public static readonly string[] Configurations = ["unmasked+spheres", "mask+spheres (today)", "tile tape+spheres", "slab tape+spheres", "tile tape, no spheres", "hit walk, mask (today)", "hit walk, tile tape"];

    /// <summary>Gets the per-configuration work.</summary>
    public SdfStudyWork[] Work { get; } = new SdfStudyWork[Configurations.Length];
    /// <summary>Gets or sets the rays marched.</summary>
    public long Rays { get; set; }
    /// <summary>Gets or sets the march samples taken.</summary>
    public long Samples { get; set; }
    /// <summary>Gets or sets the rays that hit.</summary>
    public long Hits { get; set; }
    /// <summary>Gets or sets the samples where a pruned tape's value differed from the full walk's.</summary>
    public long Mismatches { get; set; }
    /// <summary>Gets or sets the samples outside the tile's walked depth range.</summary>
    public long OutOfRange { get; set; }

    /// <summary>Adds another tally.</summary>
    /// <param name="other">The other tally.</param>
    public void Add(SdfStudyMarchTotals other) {
        for (var index = 0; (index < Work.Length); index++) {
            Work[index].Add(other: other.Work[index]);
        }
        Rays += other.Rays;
        Samples += other.Samples;
        Hits += other.Hits;
        Mismatches += other.Mismatches;
        OutOfRange += other.OutOfRange;
    }
}
/// <summary>Walks every screen tile of a view: masks its instances as the cull pass does, encloses the field over
/// depth slabs of its cone from the camera outward, finds the depth range a march can reach (from the first slab
/// that may hold a surface to the first slab wholly inside solid), and unions the slabs' live sets into the tile's
/// tape. Optionally marches a subsample of each tile's pixels through every configuration, counting the work and
/// checking that each pruned tape returns the full walk's value at every sample.</summary>
public static class SdfStudyTiles {
    private const int MaxSteps = 128;
    private const double SurfaceEpsilon = 0.001;

    /// <summary>Runs the study over every tile.</summary>
    /// <param name="tape">The tape.</param>
    /// <param name="view">The view.</param>
    /// <param name="tileSize">The tile size, in pixels.</param>
    /// <param name="marchStride">The pixel stride of the marched subsample, or zero to march nothing.</param>
    /// <param name="unmaskedBaseline">Whether the march also walks the unmasked program.</param>
    /// <param name="outward">Whether intervals round outward.</param>
    /// <returns>The tiles, the compose attribution over every walked slab, and the march totals.</returns>
    public static (SdfStudyTileResult[] Tiles, SdfStudyAttribution Attribution, SdfStudyMarchTotals March) Run(SdfStudyTape tape, SdfStudyView view, int tileSize, int marchStride, bool unmaskedBaseline, bool outward = true) {
        var tilesX = (((view.Width + tileSize) - 1) / tileSize);
        var tilesY = (((view.Height + tileSize) - 1) / tileSize);
        var results = new SdfStudyTileResult[(tilesX * tilesY)];
        var attribution = new SdfStudyAttribution();
        var march = new SdfStudyMarchTotals();
        var all = Enumerable.Range(start: 0, count: tape.Segments.Length).ToArray();
        var gate = new object();

        Parallel.For(
            fromInclusive: 0,
            toExclusive: results.Length,
            localInit: () => (new SdfStudyInterval(outward: outward, tape: tape), new SdfStudyAttribution(), new SdfStudyAttribution(), new SdfStudyMarchTotals()),
            body: (tile, _, local) => {
                var (interval, tally, scratch, totals) = local;

                results[tile] = Tile(all: (unmaskedBaseline ? all : null), interval: interval, marchStride: marchStride, scratch: scratch, tally: tally, tape: tape, tileSize: tileSize, tileX: (tile % tilesX), tileY: (tile / tilesX), totals: totals, view: view);

                return local;
            },
            localFinally: local => {
                lock (gate) {
                    attribution.Add(other: local.Item2);
                    march.Add(other: local.Item4);
                }
            }
        );

        return (results, attribution, march);
    }

    private static SdfStudyTileResult Tile(SdfStudyTape tape, SdfStudyView view, SdfStudyInterval interval, SdfStudyAttribution tally, SdfStudyAttribution scratch, SdfStudyMarchTotals totals, int tileX, int tileY, int tileSize, int marchStride, int[]? all) {
        var cone = view.Cone(tileSize: tileSize, tileX: tileX, tileY: tileY);
        var segments = view.MaskedSegments(cone: cone, tape: tape);
        var visible = 0;

        foreach (var segment in segments) {
            visible += (tape.Segments[segment].End - tape.Segments[segment].First);
        }

        var boundaries = Boundaries(chord: cone.Chord, far: view.FarDistance);
        var tileLive = new bool[tape.InstructionCount];
        var slabLive = new bool[tape.InstructionCount];
        var entry = -1;
        var exit = (boundaries.Length - 2);
        var slabs = 0;
        var slabLiveSum = 0L;

        for (var k = 0; (k < (boundaries.Length - 1)); k++) {
            var (center, radius) = Slab(view: view, cone: cone, t0: boundaries[k], t1: boundaries[(k + 1)]);

            Clear(live: slabLive, segments: segments, tape: tape);
            scratch.Clear();

            var (lo, hi) = interval.Walk(attribution: scratch, center: center, live: slabLive, radius: radius, segments: segments);

            if ((entry < 0) && (lo <= Math.Max(val1: SurfaceEpsilon, val2: (view.Footprint * boundaries[(k + 1)])))) {
                entry = k;
            }
            if (entry < 0) {
                continue;
            }
            slabs++;
            tally.Add(other: scratch);
            slabLiveSum += Or(from: slabLive, into: tileLive, segments: segments, tape: tape);
            if (hi < 0.0) {
                exit = k;

                break;
            }
        }

        var tileLiveCount = Count(live: tileLive, segments: segments, tape: tape);

        if ((marchStride > 0) && (entry >= 0)) {
            March(all: all, boundaries: boundaries, cone: cone, entry: entry, exit: exit, interval: interval, segments: segments, stride: marchStride, tape: tape, tileLive: tileLive, tileSize: tileSize, tileX: tileX, tileY: tileY, totals: totals, view: view);
        }

        return new SdfStudyTileResult(
            Entered: (entry >= 0),
            SlabLiveMean: ((slabs > 0) ? (slabLiveSum / ((double)slabs)) : 0.0),
            Slabs: slabs,
            TileLive: tileLiveCount,
            Visible: visible
        );
    }
    private static void March(SdfStudyTape tape, SdfStudyView view, SdfStudyInterval interval, SdfStudyMarchTotals totals, int[] segments, int[]? all, bool[] tileLive, double[] boundaries, int entry, int exit, (V3 Direction, double Chord, double InverseAperture) cone, int tileX, int tileY, int tileSize, int stride) {
        var tileRewrites = interval.Rewrites(live: tileLive, segments: segments);
        var slabTapes = new Dictionary<int, (bool[] Live, SdfStudyRewrite[] Rewrites)>();
        var x0 = (tileX * tileSize);
        var y0 = (tileY * tileSize);

        for (var y = y0; (y < Math.Min(val1: (y0 + tileSize), val2: view.Height)); y++) {
            if ((y % stride) != 0) {
                continue;
            }
            for (var x = x0; (x < Math.Min(val1: (x0 + tileSize), val2: view.Width)); x++) {
                if ((x % stride) != 0) {
                    continue;
                }

                var direction = view.Direction(u: ((x + 0.5) / view.Width), v: ((y + 0.5) / view.Height));
                var t = boundaries[entry];

                totals.Rays++;
                for (var step = 0; (step < MaxSteps); step++) {
                    var found = Array.BinarySearch(array: boundaries, value: t);
                    var k = ((found >= 0) ? found : (~found - 1));

                    if ((k < entry) || (k > exit) || (k >= (boundaries.Length - 1))) {
                        totals.OutOfRange++;

                        break;
                    }
                    if (!slabTapes.TryGetValue(key: k, value: out var slabTape)) {
                        var live = new bool[tape.InstructionCount];

                        var (center, radius) = Slab(view: view, cone: cone, t0: boundaries[k], t1: boundaries[(k + 1)]);

                        _ = interval.Walk(attribution: null, center: center, live: live, radius: radius, segments: segments);
                        slabTape = (live, interval.Rewrites(live: live, segments: segments));
                        slabTapes[k] = slabTape;
                    }

                    var p = (view.Eye + (direction * t));
                    var today = SdfStudyPoint.Evaluate(tape: tape, segments: segments, p: p, live: null, rewrites: null, sphereSkips: true, work: ref totals.Work[1]);
                    var tiled = SdfStudyPoint.Evaluate(tape: tape, segments: segments, p: p, live: tileLive, rewrites: tileRewrites, sphereSkips: true, work: ref totals.Work[2]);
                    var slabbed = SdfStudyPoint.Evaluate(tape: tape, segments: segments, p: p, live: slabTape.Live, rewrites: slabTape.Rewrites, sphereSkips: true, work: ref totals.Work[3]);
                    var bare = SdfStudyPoint.Evaluate(tape: tape, segments: segments, p: p, live: tileLive, rewrites: tileRewrites, sphereSkips: false, work: ref totals.Work[4]);

                    if (all is not null) {
                        _ = SdfStudyPoint.Evaluate(tape: tape, segments: all, p: p, live: null, rewrites: null, sphereSkips: true, work: ref totals.Work[0]);
                    }
                    totals.Samples++;
                    if ((tiled != today) || (slabbed != today) || (bare != today)) {
                        totals.Mismatches++;
                    }
                    if (today < Math.Max(val1: SurfaceEpsilon, val2: (view.Footprint * t))) {
                        totals.Hits++;
                        // The hit's gradient walk visits what the march's walk visits at the hit point.
                        _ = SdfStudyPoint.Evaluate(tape: tape, segments: segments, p: p, live: null, rewrites: null, sphereSkips: true, work: ref totals.Work[5]);
                        _ = SdfStudyPoint.Evaluate(tape: tape, segments: segments, p: p, live: tileLive, rewrites: tileRewrites, sphereSkips: true, work: ref totals.Work[6]);

                        break;
                    }
                    t += today;
                    if (t >= view.FarDistance) {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Returns the depth slab boundaries of a tile cone: a first slab to 0.05, then a geometric series whose
    /// ratio keeps each slab's ball about as long as it is wide, ending at the far distance.</summary>
    /// <param name="chord">The cone's chord.</param>
    /// <param name="far">The far distance.</param>
    /// <returns>The ascending boundaries.</returns>
    public static double[] Boundaries(double chord, double far) {
        var boundaries = new List<double> { 0.0, 0.05 };
        var ratio = Math.Max(val1: (1.0 + (2.0 * chord)), val2: 1.001);

        while (boundaries[^1] < far) {
            boundaries.Add(item: Math.Min(val1: (boundaries[^1] * ratio), val2: far));
        }

        return [.. boundaries];
    }
    /// <summary>Returns a ball containing the tile cone between two depths.</summary>
    /// <param name="view">The view.</param>
    /// <param name="cone">The tile cone.</param>
    /// <param name="t0">The near depth.</param>
    /// <param name="t1">The far depth.</param>
    /// <returns>The ball.</returns>
    public static (V3 Center, double Radius) Slab(SdfStudyView view, (V3 Direction, double Chord, double InverseAperture) cone, double t0, double t1) {
        var center = (view.Eye + (cone.Direction * (0.5 * (t0 + t1))));

        // A ray of the tile lies within chord * t of the axis point at depth t, and along the axis within [t0, t1].
        return (center, Math.BitIncrement(x: (((0.5 * (t1 - t0)) + (cone.Chord * t1)) * 1.000001)));
    }

    private static void Clear(bool[] live, SdfStudyTape tape, int[] segments) {
        foreach (var segment in segments) {
            Array.Clear(array: live, index: tape.Segments[segment].First, length: (tape.Segments[segment].End - tape.Segments[segment].First));
        }
    }
    private static int Or(bool[] into, bool[] from, SdfStudyTape tape, int[] segments) {
        var count = 0;

        foreach (var segment in segments) {
            for (var index = tape.Segments[segment].First; (index < tape.Segments[segment].End); index++) {
                if (from[index]) {
                    into[index] = true;
                    count++;
                }
            }
        }

        return count;
    }
    private static int Count(bool[] live, SdfStudyTape tape, int[] segments) {
        var count = 0;

        foreach (var segment in segments) {
            for (var index = tape.Segments[segment].First; (index < tape.Segments[segment].End); index++) {
                count += (live[index] ? 1 : 0);
            }
        }

        return count;
    }
}
