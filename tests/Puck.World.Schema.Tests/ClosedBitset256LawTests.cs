using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class ClosedBitset256LawTests {
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(104)]
    [InlineData(136)]
    [InlineData(256)]
    [Theory]
    public void CompleteDrawConservesEveryEntryAndResumesAtWordBoundaries(int count) {
        var generator = new StateGenerator(
            Source: GeneratorSource.WeightedNumeric,
            Mode: GeneratorMode.WithoutReplacement,
            Weighted: Enumerable.Range(
                count: count,
                start: 0
            ).Select(selector: i => new GeneratorWeightedNumeric(
                Value: i,
                Weight: 1
            )).ToArray()
        );
        IReadOnlyList<ClosedBitset256>? masks = null;
        var seen = new HashSet<long>();

        for (var cursor = 0; (cursor < count); cursor++) {
            Assert.True(
                condition: GeneratorEngine.TryFire(
                    generator,
                    CellKind.Int,
                    123,
                    7,
                    cursor,
                    masks,
                    out var draw,
                    out var reason
                ),
                userMessage: reason
            );
            Assert.True(condition: seen.Add(item: draw.Numeric!.Value));
            Assert.Equal(
                (cursor + 1),
                draw.Masks![0].Count
            );
            Assert.True(
                condition: GeneratorEngine.TryFire(
                    generator,
                    CellKind.Int,
                    123,
                    7,
                    cursor,
                    masks,
                    out var replay,
                    out reason
                ),
                userMessage: reason
            );
            Assert.Equal(
                draw.Numeric,
                replay.Numeric
            );
            Assert.Equal(
                draw.Masks,
                replay.Masks
            );
            masks = draw.Masks;
        }
        Assert.False(condition: GeneratorEngine.TryFire(
            generator,
            CellKind.Int,
            123,
            7,
            count,
            masks,
            out _,
            out _
        ));
        Assert.False(condition: GeneratorEngine.TryCheckBatchCapacity(
            generator: generator,
            masks: masks,
            reason: out _,
            sampleCount: 1
        ));
    }
    [Fact]
    public void EveryBitHasIndependentMembershipAndCanonicalText() {
        var set = default(ClosedBitset256);

        for (var bit = 0; (bit < 256); bit++) {
            Assert.False(condition: set.Contains(index: bit));
            set = set.Add(index: bit);
            Assert.Equal(
                (bit + 1),
                set.Count
            );
            Assert.True(condition: set.Fits(count: (bit + 1)));
            Assert.False(condition: set.Fits(count: bit));
            Assert.True(condition: ClosedBitset256.TryParse(
                text: set.ToString(),
                value: out var parsed
            ));
            Assert.Equal(
                actual: parsed,
                expected: set
            );
        }
        Assert.Equal(
            new string(
                c: 'F',
                count: 64
            ),
            set.ToString()
        );
        Assert.False(condition: set.Contains(index: -1));
        Assert.False(condition: set.Contains(index: 256));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => set.Add(index: 256));
        Assert.False(condition: ClosedBitset256.TryParse(
            text: "-1",
            value: out _
        ));
        Assert.False(condition: ClosedBitset256.TryParse(
            text: new string(
                c: 'G',
                count: 64
            ),
            value: out _
        ));
    }
}
