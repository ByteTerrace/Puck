using System.Numerics;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldStateAdvance.ComputeCurrentValue"/> — the compiled signed-64-bit allocation and
/// the exact <see cref="BigInteger"/> allocation behind it must be one function of (rate, kind, elapsed). Every read is
/// checked against <c>⌊elapsed · |rate| · scale / denominator⌋</c> formed here in <see cref="BigInteger"/> arithmetic
/// that shares no line with the subject, over rates the bounded form holds and rates it must decline.
/// </summary>
public sealed class StateAdvanceComputeLawTests {
    private static WorldStateRow Row(CellKind kind, long? min = null, long? max = null) => new(
        Name: WorldCellName.Parse(candidate: "gauge"),
        Kind: kind,
        Min: min,
        Max: max,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)]
    );
    private static long Oracle(WorldStateRow row, WorldStateAdvance advance, long baseValue, ulong tick) {
        var epoch = (ulong)Math.Max(val1: advance.EpochTick, val2: 0L);
        var elapsed = ((tick <= epoch) ? BigInteger.Zero : new BigInteger(value: (tick - epoch)));
        var scale = ((row.Kind == CellKind.Fixed) ? (BigInteger.One << FixedQ4816.FractionBitCount) : BigInteger.One);
        var magnitude = BigInteger.Divide(dividend: (elapsed * BigInteger.Abs(value: advance.RateNumerator) * scale), divisor: advance.RateDenominator);
        var raw = (baseValue + ((advance.RateNumerator < 0) ? -magnitude : magnitude));
        var saturated = ((raw > long.MaxValue) ? long.MaxValue : ((raw < long.MinValue) ? long.MinValue : (long)raw));

        return row.ClampToEnvelope(value: saturated);
    }

    public static IEnumerable<object[]> Rates() {
        yield return [1L, 57600L];
        yield return [1L, 24L];
        yield return [-3L, 7L];
        yield return [5L, 1L];
        yield return [-7L, 3L];
        yield return [long.MaxValue, 1L];
        yield return [long.MinValue + 1L, 3L];
        yield return [(1L << 40), (1L << 20) + 1L];
        yield return [-(1L << 47), 1L];
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void ComputeCurrentValue_MatchesTheExactAllocation_OnEveryKindAndEpoch(long numerator, long denominator) {
        foreach (var kind in new[] { CellKind.Int, CellKind.Fixed }) {
            foreach (var epoch in new long[] { 0L, 17L, 1000L }) {
                var advance = new WorldStateAdvance(EpochTick: epoch, RateDenominator: denominator, RateNumerator: numerator);
                var row = Row(kind: kind);

                foreach (var baseValue in new long[] { 0L, 300L, -(1L << 20), (1L << 40) }) {
                    foreach (var tick in new ulong[] { 0UL, 1UL, 16UL, 17UL, 18UL, 240UL, 999UL, 1000UL, 1001UL, 57599UL, 57600UL, 57601UL, 1_000_000UL, 4_294_967_296UL, ulong.MaxValue }) {
                        Assert.Equal(expected: Oracle(advance: advance, baseValue: baseValue, row: row, tick: tick), actual: advance.ComputeCurrentValue(baseValue: baseValue, currentTick: tick, row: row));
                    }
                }
            }
        }
    }
    [Fact]
    public void ComputeCurrentValue_ClampsIntoTheEnvelope_AfterTheExactSum() {
        var advance = new WorldStateAdvance(EpochTick: 0, RateDenominator: 1, RateNumerator: 3);
        var row = Row(kind: CellKind.Int, max: 100L, min: -5L);

        Assert.Equal(expected: 30L, actual: advance.ComputeCurrentValue(baseValue: 0L, currentTick: 10UL, row: row));
        Assert.Equal(expected: 100L, actual: advance.ComputeCurrentValue(baseValue: 0L, currentTick: 1000UL, row: row));
        Assert.Equal(expected: 100L, actual: advance.ComputeCurrentValue(baseValue: long.MaxValue, currentTick: 1000UL, row: row));

        var drain = new WorldStateAdvance(EpochTick: 0, RateDenominator: 1, RateNumerator: -3);

        Assert.Equal(expected: -5L, actual: drain.ComputeCurrentValue(baseValue: 0L, currentTick: 1000UL, row: row));
        Assert.Equal(expected: -5L, actual: drain.ComputeCurrentValue(baseValue: long.MinValue, currentTick: 1000UL, row: row));
    }
    [Fact]
    public void ComputeCurrentValue_IsStableAcrossAWithCopyThatChangesTheRate() {
        var row = Row(kind: CellKind.Fixed);
        var slow = new WorldStateAdvance(EpochTick: 0, RateDenominator: 57600, RateNumerator: 1);
        var slowValue = slow.ComputeCurrentValue(baseValue: 0L, currentTick: 100_000UL, row: row);
        var fast = (slow with { RateNumerator = 1000 });

        Assert.Equal(expected: Oracle(advance: slow, baseValue: 0L, row: row, tick: 100_000UL), actual: slowValue);
        Assert.Equal(expected: Oracle(advance: fast, baseValue: 0L, row: row, tick: 100_000UL), actual: fast.ComputeCurrentValue(baseValue: 0L, currentTick: 100_000UL, row: row));
        Assert.Equal(expected: slowValue, actual: slow.ComputeCurrentValue(baseValue: 0L, currentTick: 100_000UL, row: row));
    }

    [Fact]
    public void ComputeCurrentValue_DoesNotChangeRecordEqualityOrHashCode() {
        var row = Row(kind: CellKind.Fixed);
        var left = new WorldStateAdvance(EpochTick: 7, RateDenominator: 3, RateNumerator: -5);
        var right = new WorldStateAdvance(EpochTick: 7, RateDenominator: 3, RateNumerator: -5);
        var hashBefore = left.GetHashCode();

        Assert.Equal(expected: right, actual: left);
        Assert.Equal(expected: right.GetHashCode(), actual: hashBefore);

        _ = left.ComputeCurrentValue(baseValue: 100L, currentTick: 17UL, row: row);

        Assert.Equal(expected: right, actual: left);
        Assert.Equal(expected: hashBefore, actual: left.GetHashCode());
        Assert.Equal(expected: right.GetHashCode(), actual: left.GetHashCode());
    }
}
