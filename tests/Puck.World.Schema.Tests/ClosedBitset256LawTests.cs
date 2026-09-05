using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class ClosedBitset256LawTests {
    [Fact]
    public void EveryBitHasIndependentMembershipAndCanonicalText() {
        var set = default(ClosedBitset256);
        for (var bit = 0; bit < 256; bit++) {
            Assert.False(set.Contains(bit));
            set = set.Add(bit);
            Assert.Equal(bit + 1, set.Count);
            Assert.True(set.Fits(bit + 1));
            Assert.False(set.Fits(bit));
            Assert.True(ClosedBitset256.TryParse(set.ToString(), out var parsed));
            Assert.Equal(set, parsed);
        }
        Assert.Equal(new string('F', 64), set.ToString());
        Assert.False(set.Contains(-1));
        Assert.False(set.Contains(256));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.Add(256));
        Assert.False(ClosedBitset256.TryParse("-1", out _));
        Assert.False(ClosedBitset256.TryParse(new string('G', 64), out _));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(104)]
    [InlineData(136)]
    [InlineData(256)]
    public void CompleteDrawConservesEveryEntryAndResumesAtWordBoundaries(int count) {
        var generator = new WorldGenerator(Source: WorldGeneratorSource.WeightedNumeric,
            Mode: WorldGeneratorMode.WithoutReplacement,
            Weighted: Enumerable.Range(0, count).Select(i => new WorldGeneratorWeightedNumeric(Value: i, Weight: 1)).ToArray());
        IReadOnlyList<ClosedBitset256>? masks = null;
        var seen = new HashSet<long>();
        for (var cursor = 0; cursor < count; cursor++) {
            Assert.True(WorldGeneratorEngine.TryFire(generator, CellKind.Int, 123, 7, cursor, masks, out var draw, out var reason), reason);
            Assert.True(seen.Add(draw.Numeric!.Value));
            Assert.Equal(cursor + 1, draw.Masks![0].Count);
            Assert.True(WorldGeneratorEngine.TryFire(generator, CellKind.Int, 123, 7, cursor, masks, out var replay, out reason), reason);
            Assert.Equal(draw.Numeric, replay.Numeric);
            Assert.Equal(draw.Masks, replay.Masks);
            masks = draw.Masks;
        }
        Assert.False(WorldGeneratorEngine.TryFire(generator, CellKind.Int, 123, 7, count, masks, out _, out _));
        Assert.False(WorldGeneratorEngine.TryCheckBatchCapacity(generator, masks, 1, out _));
    }
}
