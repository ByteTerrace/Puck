using System.Globalization;
using System.Text;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests.FidgetStudy;

/// <summary>Measures how much of each studied world's render program interval analysis could prune per screen tile,
/// beyond what the per-tile instance mask already removes, and what that would save per march sample beyond the
/// per-sample bounding-sphere skips. A measurement, run on request:
/// <c>dotnet test tests/Puck.World.Tests -c Release --filter FullyQualifiedName~FidgetStudy.SdfStudyPruning -- xUnit.Explicit=on</c>.</summary>
public sealed class SdfStudyPruningExperiment {
    private static readonly int[] TileSizes = [8, 16, 32];

    [Fact(Explicit = true)]
    public void PerTilePruningOfTheStudiedPrograms() {
        var report = new StringBuilder();

        foreach (var (name, path) in SdfStudyScene.Worlds) {
            var scene = SdfStudyScene.Load(name: name, relativePath: path);
            var tape = new SdfStudyTape(program: scene.Program, transforms: scene.Transforms);
            var faithful = SdfStudyPoint.IsFaithful(first: out var first, tape: tape);
            var shapes = tape.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.ShapeBlend));

            foreach (var (viewName, view) in ViewsOf(world: name)) {
                report.AppendLine();
                report.AppendLine(value: $"== {name} from {viewName} (eye {view.Eye.X},{view.Eye.Y},{view.Eye.Z}, {view.Width}x{view.Height}, far {view.FarDistance}): {tape.InstructionCount} instructions, {shapes} shapes, {tape.Segments.Length} segments, {tape.Instances.Length} instances; point transcription {(faithful ? "faithful" : $"approximate ({first})")}");
                foreach (var tileSize in TileSizes) {
                    var (tiles, attribution, march) = SdfStudyTiles.Run(
                        marchStride: 8,
                        tape: tape,
                        tileSize: tileSize,
                        unmaskedBaseline: ((tileSize == 16) && (tape.InstructionCount <= 4000)),
                        view: view
                    );

                    Describe(report: report, tileSize: tileSize, tiles: tiles, attribution: attribution, march: march, instructions: tape.InstructionCount);
                }
            }
        }
        SdfStudyReport.Write(name: "pruning.txt", text: report.ToString());
    }

    // The counters workload's view for every world; the parity world's stations lie far from it, so parity also
    // reads from its lattice, noise and vocabulary station cameras at the same render extent.
    private static IEnumerable<(string Name, SdfStudyView View)> ViewsOf(string world) {
        yield return ("counters-cam", SdfStudyView.CountersFloor);
        if (world == "parity") {
            yield return ("lattice-cam", new SdfStudyView(eye: new V3(X: 288, Y: 6, Z: -10), farDistance: 40.0, height: 810, target: new V3(X: 288, Y: 0.5, Z: 0), verticalFov: 0.85, width: 1440));
            yield return ("noise-cam", new SdfStudyView(eye: new V3(X: 400, Y: 1, Z: -4), farDistance: 40.0, height: 810, target: new V3(X: 400, Y: 1, Z: 0), verticalFov: 0.75, width: 1440));
            yield return ("vocabulary-cam", new SdfStudyView(eye: new V3(X: 604, Y: 9, Z: -26), farDistance: 40.0, height: 810, target: new V3(X: 604, Y: 2, Z: 0), verticalFov: 1.4, width: 1440));
        }
    }
    private static void Describe(StringBuilder report, int tileSize, SdfStudyTileResult[] tiles, SdfStudyAttribution attribution, SdfStudyMarchTotals march, int instructions) {
        var entered = tiles.Where(predicate: tile => (tile.Entered && (tile.Visible > 0))).ToArray();
        var tilePruned = entered.Select(selector: tile => (1.0 - (tile.TileLive / ((double)tile.Visible)))).Order().ToArray();
        var slabPruned = entered.Select(selector: tile => (1.0 - (tile.SlabLiveMean / tile.Visible))).Order().ToArray();
        var visible = entered.Sum(selector: tile => ((long)tile.Visible));
        var tileLive = entered.Sum(selector: tile => ((long)tile.TileLive));
        var slabLive = entered.Sum(selector: tile => tile.SlabLiveMean);

        report.AppendLine(value: $"  -- {tileSize}px tiles: {tiles.Length} tiles, {entered.Length} reach a surface; mean instructions per entered tile: program {instructions}, after mask {Mean(values: entered.Select(selector: tile => ((double)tile.Visible)))}, tile tape {Mean(values: entered.Select(selector: tile => ((double)tile.TileLive)))}, slab tape {Mean(values: entered.Select(selector: tile => tile.SlabLiveMean))}; mean slabs walked {Mean(values: entered.Select(selector: tile => ((double)tile.Slabs)))}");
        report.AppendLine(value: $"     mask removes {Percent(fraction: (1.0 - (visible / ((double)Math.Max(val1: 1L, val2: (((long)entered.Length) * instructions))))))} of the program on entered tiles");
        report.AppendLine(value: $"     pruned beyond the mask, tile tape: weighted {Percent(fraction: (1.0 - (tileLive / ((double)Math.Max(val1: 1L, val2: visible)))))}, per-tile mean {Percent(fraction: Mean(values: tilePruned))}, p10 {Percent(fraction: Quantile(q: 0.10, sorted: tilePruned))} p25 {Percent(fraction: Quantile(q: 0.25, sorted: tilePruned))} p50 {Percent(fraction: Quantile(q: 0.50, sorted: tilePruned))} p75 {Percent(fraction: Quantile(q: 0.75, sorted: tilePruned))} p90 {Percent(fraction: Quantile(q: 0.90, sorted: tilePruned))}");
        report.AppendLine(value: $"     pruned beyond the mask, slab tape: weighted {Percent(fraction: (1.0 - (slabLive / Math.Max(val1: 1.0, val2: visible))))}, per-tile mean {Percent(fraction: Mean(values: slabPruned))}, p10 {Percent(fraction: Quantile(q: 0.10, sorted: slabPruned))} p50 {Percent(fraction: Quantile(q: 0.50, sorted: slabPruned))} p90 {Percent(fraction: Quantile(q: 0.90, sorted: slabPruned))}");
        report.AppendLine(value: ("     tile-tape histogram (deciles of pruned fraction): " + string.Join(separator: " ", values: Histogram(sorted: tilePruned))));
        report.AppendLine(value: $"     composes over walked slabs: {attribution.Shapes}, dead {Percent(fraction: (attribution.DeadShapes / ((double)Math.Max(val1: 1L, val2: attribution.Shapes))))}; live but smooth-blocked {attribution.SmoothBlocked}; live because unbounded {attribution.UnboundedLive}");
        report.AppendLine(value: ("     dead rate by blend: " + string.Join(separator: ", ", values: Enumerable.Range(count: 32, start: 0)
            .Where(predicate: row => (attribution.ByBlend[row, 0] > 0))
            .Select(selector: row => $"{((SdfBlendOp)row)} {Percent(fraction: (attribution.ByBlend[row, 1] / ((double)attribution.ByBlend[row, 0])))} of {attribution.ByBlend[row, 0]}"))));
        report.AppendLine(value: ("     dead rate by chain feature: " + string.Join(separator: ", ", values: Enumerable.Range(start: 0, count: SdfStudyAttribution.FlagNames.Length)
            .Where(predicate: row => (attribution.ByFlag[row, 0] > 0))
            .Select(selector: row => $"{SdfStudyAttribution.FlagNames[row]} {Percent(fraction: (attribution.ByFlag[row, 1] / ((double)attribution.ByFlag[row, 0])))} of {attribution.ByFlag[row, 0]}"))));
        if (march.Samples > 0) {
            report.AppendLine(value: $"     march (every 8th pixel each axis): {march.Rays} rays, {march.Samples} samples, {march.Hits} hits, {march.Mismatches} pruned-value mismatches, {march.OutOfRange} out-of-range samples");
            for (var index = 0; (index < 5); index++) {
                var work = march.Work[index];

                if (work.Segments == 0) {
                    continue;
                }
                report.AppendLine(value: $"       {SdfStudyMarchTotals.Configurations[index],-24} per sample: ops {(work.Ops / ((double)march.Samples)):F2}, shapes evaluated {(work.Shapes / ((double)march.Samples)):F2}, segments entered {(work.Segments / ((double)march.Samples)):F2}, segment sphere skips {(work.SegmentSkips / ((double)march.Samples)):F2}, shape sphere skips {(work.ShapeSkips / ((double)march.Samples)):F2}");
            }

            if (march.Hits > 0) {
                foreach (var index in ((int[])[5, 6])) {
                    var work = march.Work[index];
                    // A forward-mode walk pays one gradient per evaluated shape: analytic, or four shape taps for the
                    // tail; selecting the winner's gradient only needs it where the compose changed the accumulator.
                    var eager = ((work.Shapes - work.TailShapes) + (4 * work.TailShapes));
                    var lazy = ((work.Influential - work.TailInfluential) + (4 * work.TailInfluential));

                    report.AppendLine(value: $"       {SdfStudyMarchTotals.Configurations[index],-24} per hit: shapes evaluated {(work.Shapes / ((double)march.Hits)):F2} (four-tap tail {(work.TailShapes / ((double)march.Hits)):F2}), changed the accumulator {(work.Influential / ((double)march.Hits)):F2}; gradient shape-evaluations eager {(eager / ((double)march.Hits)):F2}, winner-only {(lazy / ((double)march.Hits)):F2}");
                }
            }

            var today = march.Work[1];

            report.AppendLine(value: $"       beyond today: tile tape saves {Percent(fraction: (1.0 - (march.Work[2].Shapes / ((double)Math.Max(val1: 1L, val2: today.Shapes)))))} of shape evaluations and {Percent(fraction: (1.0 - (march.Work[2].Ops / ((double)Math.Max(val1: 1L, val2: today.Ops)))))} of dispatched ops; slab tape saves {Percent(fraction: (1.0 - (march.Work[3].Shapes / ((double)Math.Max(val1: 1L, val2: today.Shapes)))))} and {Percent(fraction: (1.0 - (march.Work[3].Ops / ((double)Math.Max(val1: 1L, val2: today.Ops)))))}");
        }
    }
    private static string[] Histogram(double[] sorted) {
        var buckets = new int[10];

        foreach (var value in sorted) {
            buckets[Math.Clamp(max: 9, min: 0, value: ((int)(value * 10.0)))]++;
        }

        return [.. buckets.Select(selector: (count, index) => $"{(index * 10)}-{((index + 1) * 10)}%:{count}")];
    }
    private static double Quantile(double[] sorted, double q) => ((sorted.Length == 0) ? 0.0 : sorted[Math.Min(val1: (sorted.Length - 1), val2: ((int)(q * sorted.Length)))]);
    private static double Mean(IEnumerable<double> values) {
        var array = values.ToArray();

        return ((array.Length == 0) ? 0.0 : Math.Round(value: array.Average(), digits: 2));
    }
    private static string Percent(double fraction) => ((fraction * 100.0).ToString(format: "F1", provider: CultureInfo.InvariantCulture) + "%");
}
