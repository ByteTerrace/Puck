using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class SdfTapePredictionLawTests {
    private const int StudyDepthUnits = (1 << 20);

    private sealed record StudyPartition(string Name, int[] DepthUnits) {
        public int SlabCount => (DepthUnits.Length - 1);

        public int Width(int slab) => (DepthUnits[(slab + 1)] - DepthUnits[slab]);
    }
    private struct StudyTotals {
        public long Considered, Dropped, Unsupported, Overlap;
        public long DepthConsidered, DepthDropped, DepthUnsupported;

        public void Add(Sample sample, int depthUnits) {
            checked {
                Considered += sample.Considered;
                Dropped += sample.Dropped;
                Unsupported += sample.UnsupportedLive;
                Overlap += sample.OverlapLive;
                DepthConsidered += (sample.Considered * depthUnits);
                DepthDropped += (sample.Dropped * depthUnits);
                DepthUnsupported += (sample.UnsupportedLive * depthUnits);
            }
        }
    }

    private static StudyPartition[] StudyPartitions() {
        var quartic = PowerPartition(power: 4, slabs: Slabs);
        var partitions = new[] {
            PowerPartition(power: 1, slabs: Slabs),
            PowerPartition(power: 2, slabs: Slabs),
            quartic,
            SplitFarPartition(source: quartic, subdivisions: 2),
            PowerPartition(power: 4, slabs: 16),
            SplitFarPartition(source: quartic, subdivisions: 4),
            PowerPartition(power: 4, slabs: 32),
        };

        Assert.Equal(actual: SdfWorldPackage.TapeSlabCount, expected: Slabs);
        foreach (var partition in partitions) {
            Assert.Equal(expected: 0, actual: partition.DepthUnits[0]);
            Assert.Equal(expected: StudyDepthUnits, actual: partition.DepthUnits[^1]);
            for (var slab = 0; (slab < partition.SlabCount); slab++) {
                Assert.True(condition: (partition.Width(slab: slab) > 0), userMessage: $"{partition.Name} must cover depth without an empty or reversed slab.");
            }
        }
        return partitions;
    }
    private static StudyPartition PowerPartition(int slabs, int power) {
        var denominator = IntegerPower(power: power, value: slabs);

        Assert.Equal(actual: (StudyDepthUnits % denominator), expected: 0);
        var units = new int[(slabs + 1)];

        for (var boundary = 0; (boundary <= slabs); boundary++) {
            units[boundary] = checked((IntegerPower(power: power, value: boundary) * (StudyDepthUnits / denominator)));
        }
        var spacing = power switch { 1 => "uniform", 2 => "quadratic", _ => "quartic" };

        return new StudyPartition(DepthUnits: units, Name: $"{spacing}-{slabs}");
    }
    private static int IntegerPower(int value, int power) {
        var result = 1;

        for (var index = 0; (index < power); index++) { result = checked((result * value)); }
        return result;
    }
    private static StudyPartition SplitFarPartition(StudyPartition source, int subdivisions) {
        var units = new List<int> { 0 };

        for (var slab = 0; (slab < source.SlabCount); slab++) {
            var count = ((slab < (source.SlabCount / 2)) ? 1 : subdivisions);
            var width = source.Width(slab: slab);

            Assert.Equal(actual: (width % count), expected: 0);
            for (var split = 1; (split <= count); split++) {
                units.Add(item: (source.DepthUnits[slab] + ((width / count) * split)));
            }
        }
        Assert.Equal(expected: source.DepthUnits.AsSpan(length: 5, start: 0).ToArray(),
            actual: units.Take(count: 5).ToArray());
        return new StudyPartition(Name: $"quartic-{source.SlabCount}-far-split-{subdivisions}", DepthUnits: [.. units]);
    }
    private void ReportStudy(SdfProgram program, int tiles, StudyPartition[] partitions, StudyTotals[] totals) {
        var baseline = totals[2];
        var baselineWords = SdfWorldPackage.SegmentTapeWordCountFor(segments: program.SkipSegmentCount, tokens: program.TapeTokenCount);
        var baselineBytes = checked(((((long)baselineWords) * tiles) * sizeof(uint)));

        Assert.Equal(actual: ((baselineWords - 1) % SdfWorldPackage.TapeSlabCount), expected: 0);
        var wordsPerSlab = ((baselineWords - 1) / SdfWorldPackage.TapeSlabCount);

        output.WriteLine(message: "Partition experiments change only CPU ball placement. Production stays quartic-8. Candidate fractions count each slab equally; depth fractions use exact dyadic depth widths. Neither predicts actual beam entries, hit distribution, GPU bits, or march-weighted savings.");
        output.WriteLine(message: "Centre-evaluation counts assume one candidate evaluation per masked ShapeBlend per slab. Tape bytes project the current packed layout at this extent, including every slab ball and mask; no production allocation changes.");
        for (var index = 0; (index < partitions.Length); index++) {
            var partition = partitions[index];
            var count = totals[index];
            var words = checked((1 + (partition.SlabCount * wordsPerSlab)));
            var bytes = checked(((((long)words) * tiles) * sizeof(uint)));
            var candidateFraction = (((double)count.Dropped) / count.Considered);
            var depthFraction = (((double)count.DepthDropped) / count.DepthConsidered);
            var baselineDepthFraction = (((double)baseline.DepthDropped) / baseline.DepthConsidered);

            Assert.Equal(actual: ((count.Dropped + count.Unsupported) + count.Overlap), expected: count.Considered);
            Assert.Equal(actual: count.DepthConsidered, expected: baseline.DepthConsidered);
            Assert.Equal(expected: (baseline.Considered * partition.SlabCount), actual: (count.Considered * Slabs));
            output.WriteLine(message: $"experiment {partition.Name} slabs={partition.SlabCount} dropped={count.Dropped}/{count.Considered} fraction={candidateFraction:P6} depthDropped={count.DepthDropped}/{count.DepthConsidered} depthFraction={depthFraction:P6} depthDeltaPoints={((depthFraction - baselineDepthFraction) * 100):F6} centreEvaluations={count.Considered} extraEvaluations={(count.Considered - baseline.Considered)} evaluationRatio={(((double)count.Considered) / baseline.Considered):F6} tapeWordsPerTile={words} tapeBytes={bytes} extraTapeBytes={(bytes - baselineBytes)} tapeRatio={(((double)bytes) / baselineBytes):F6} unsupported={count.Unsupported} depthUnsupported={count.DepthUnsupported}");
        }
    }
}
