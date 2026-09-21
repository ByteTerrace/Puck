using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a pool field's raw read answers the number its carrier read holds, refuses exactly
/// where the carrier read refuses, and allocates nothing.</summary>
public sealed class StatePoolRawReadLawTests {
    private static readonly ArenaTime ClaimTime = ArenaTime.At(engineTick: 50_400UL, tick: 3UL);
    private static readonly ArenaTime OneSecondLater = ClaimTime with { EngineTick = 100_800UL };

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateArena Build() {
        var section = Section();

        return new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);
    }
    private static StateSection Section() => new(
            Records: [new StateRecord(Name: Name(value: "item"), Fields: [
                new StatePoolField(Name: Name(value: "count"), Default: CellValue.Int(value: 7)),
                new StatePoolField(Name: Name(value: "speed"), Kind: CellKind.Fixed, Default: CellValue.Fixed(rawBits: 98_304L)),
                new StatePoolField(Name: Name(value: "ready"), Kind: CellKind.Bool, Default: CellValue.Bool(value: true)),
                new StatePoolField(Name: Name(value: "label"), Kind: CellKind.Text, Default: CellValue.Text(value: "x")),
                new StatePoolField(Name: Name(value: "clock"), Advance: new StateAdvance(PerSecondDenominator: 1L, PerSecondNumerator: 1L)),
            ])],
            Pools: [new StatePool(Name: Name(value: "items"), Record: Name(value: "item"), Capacity: 130)]
        );

    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [Theory]
    public void ANumericFieldReadsTheNumberItsCarrierHolds(int fieldOrdinal) {
        var arena = Build();

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out var reason, time: ClaimTime), userMessage: reason);
        Assert.True(condition: arena.TryRead(fieldOrdinal: fieldOrdinal, handle: handle, value: out var stored));
        Assert.True(condition: arena.TryReadRaw(fieldOrdinal: fieldOrdinal, handle: handle, raw: out var raw));
        Assert.Equal(
            actual: raw,
            expected: stored.Raw
        );
        Assert.True(condition: arena.TryReadLive(fieldOrdinal: fieldOrdinal, handle: handle, time: OneSecondLater, value: out var live));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: fieldOrdinal, handle: handle, raw: out var liveRaw, time: OneSecondLater));
        Assert.Equal(
            actual: liveRaw,
            expected: live.Raw
        );
    }
    [Fact]
    public void ATraitedFieldAdvancesThroughItsRawReadAndAStoredFieldDoesNot() {
        var arena = Build();

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out _, time: ClaimTime));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 4, handle: handle, raw: out var atClaim, time: ClaimTime));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 4, handle: handle, raw: out var later, time: OneSecondLater));
        Assert.Equal(
            actual: (later - atClaim),
            expected: 1L
        );
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: handle, raw: out var count, time: OneSecondLater));
        Assert.Equal(
            actual: count,
            expected: 7L
        );
    }
    [Fact]
    public void ARawLiveReadKeepsFixedAndBooleanEncodingsAndAdvancesTheTimedIntegerExactly() {
        var arena = Build();

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out _, time: ClaimTime));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: handle, raw: out var count, time: OneSecondLater));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 1, handle: handle, raw: out var fixedBits, time: OneSecondLater));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 2, handle: handle, raw: out var ready, time: OneSecondLater));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 4, handle: handle, raw: out var clock, time: OneSecondLater));

        Assert.Equal(expected: 7L, actual: count);
        Assert.Equal(expected: 98_304L, actual: fixedBits);
        Assert.Equal(expected: 1L, actual: ready);
        Assert.Equal(expected: 1L, actual: clock);
    }
    [Fact]
    public void ARawReadRefusesWhereTheCarrierReadDoesAndOnATextField() {
        var arena = Build();

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out _));
        Assert.False(condition: arena.TryReadRaw(fieldOrdinal: 3, handle: handle, raw: out var text));
        Assert.Equal(
            actual: text,
            expected: 0L
        );
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 3, handle: handle, raw: out _, time: OneSecondLater));
        Assert.False(condition: arena.TryReadRaw(fieldOrdinal: 5, handle: handle, raw: out _));
        Assert.False(condition: arena.TryReadRaw(fieldOrdinal: -1, handle: handle, raw: out _));
        Assert.False(condition: arena.TryReadRaw(fieldOrdinal: 0, handle: default, raw: out _));
        Assert.True(condition: arena.TryRelease(handle: handle, reason: out _));
        Assert.False(condition: arena.TryReadRaw(fieldOrdinal: 0, handle: handle, raw: out _));
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: handle, raw: out _, time: OneSecondLater));
        Assert.True(condition: arena.TryClaim(handle: out var replacement, poolOrdinal: 0, reason: out _));
        Assert.Equal(
            actual: replacement.Slot,
            expected: handle.Slot
        );
        Assert.False(condition: arena.TryReadRaw(fieldOrdinal: 0, handle: handle, raw: out _));
        Assert.True(condition: arena.TryReadRaw(fieldOrdinal: 0, handle: replacement, raw: out _));
    }
    [Fact]
    public void ARawReadAddressesEverySlotAcrossAWordBoundaryAndSkipsAReleasedOne() {
        var arena = Build();
        var handles = new StateInstanceHandle[130];

        for (var slot = 0; (slot < handles.Length); slot++) {
            Assert.True(condition: arena.TryClaim(handle: out handles[slot], poolOrdinal: 0, reason: out _));
            Assert.True(condition: arena.TryWrite(fieldOrdinal: 0, handle: handles[slot], reason: out _, value: CellValue.Int(value: (1000L + slot))));
        }
        Assert.True(condition: arena.TryRelease(handle: handles[64], reason: out _));
        for (var slot = 0; (slot < handles.Length); slot++) {
            Assert.Equal(
                actual: arena.TryReadRaw(fieldOrdinal: 0, handle: handles[slot], raw: out var raw),
                expected: (slot != 64)
            );
            Assert.Equal(
                actual: raw,
                expected: ((slot != 64) ? (1000L + slot) : 0L)
            );
        }
    }
    [Fact]
    public void ARawReadUsesTheRelayoutFieldAddressAfterFieldOrderAndRetainsTraits() {
        var section = Section();
        var arena = Build();

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out _, time: ClaimTime));
        var replacement = section with {
            Records = [new StateRecord(Name: Name(value: "item"), Fields: [
                new StatePoolField(Name: Name(value: "clock"), Advance: new StateAdvance(PerSecondDenominator: 1L, PerSecondNumerator: 1L)),
                new StatePoolField(Name: Name(value: "label"), Kind: CellKind.Text, Default: CellValue.Text(value: "x")),
                new StatePoolField(Name: Name(value: "ready"), Kind: CellKind.Bool, Default: CellValue.Bool(value: true)),
                new StatePoolField(Name: Name(value: "speed"), Kind: CellKind.Fixed, Default: CellValue.Fixed(rawBits: 98_304L)),
                new StatePoolField(Name: Name(value: "count"), Default: CellValue.Int(value: 7L)),
            ])],
        };

        Assert.True(condition: arena.TryRelayout(catalog: StateCatalog.Compile(section: replacement), section: replacement, time: ClaimTime, reason: out var reason), userMessage: reason);
        Assert.True(condition: arena.TryResolvePoolSlot(poolOrdinal: 0, slot: handle.Slot, handle: out var relaid));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: relaid, raw: out var clock, time: OneSecondLater));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 4, handle: relaid, raw: out var count, time: OneSecondLater));

        Assert.Equal(expected: 1L, actual: clock);
        Assert.Equal(expected: 7L, actual: count);
    }
    [Fact]
    public void ARawReadRefusesVectorFieldsForOrdinaryAndPairInstances() {
        Assert.True(condition: StateVector.TryCreate(components: new sbyte[] { 127, 0, 0, 0, 0, 0, 0, 0 }, error: out var vectorError, vector: out var vector), userMessage: vectorError);
        var record = new StateRecord(Name: Name(value: "item"), Fields: [
            new StatePoolField(Name: Name(value: "direction"), Kind: CellKind.Vector, Space: Name(value: "space8"), Dimensions: 8, Default: CellValue.Vector(components: vector.Memory)),
        ]);
        var section = new StateSection(
            Spaces: [new StateSpace(Name: Name(value: "space8"), Model: "model", Revision: "r1", Dimensions: 8)],
            Records: [record],
            Pools: [new StatePool(Name: Name(value: "nodes"), Record: record.Name, Capacity: 2)],
            PairPools: [new StatePairPool(Name: Name(value: "edges"), Record: record.Name, LeftPool: Name(value: "nodes"), RightPool: Name(value: "nodes"), MaxLive: 1)]
        );
        var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);

        Assert.True(condition: arena.TryClaim(handle: out var left, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaim(handle: out var right, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaimPair(handle: out var pair, leftHandle: left, poolOrdinal: 1, reason: out _, rightHandle: right));
        Assert.False(condition: arena.TryReadRaw(fieldOrdinal: 0, handle: left, raw: out var ordinary));
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: left, raw: out var ordinaryLive, time: OneSecondLater));
        Assert.False(condition: arena.TryReadRaw(fieldOrdinal: 0, handle: pair, raw: out var paired));
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: pair, raw: out var pairedLive, time: OneSecondLater));
        Assert.Equal(actual: ordinary, expected: 0L);
        Assert.Equal(expected: 0L, actual: ordinaryLive);
        Assert.Equal(actual: paired, expected: 0L);
        Assert.Equal(expected: 0L, actual: pairedLive);
    }
    [Fact]
    public void AWarmedRawReadAllocatesNothing() {
        var arena = Build();

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out _, time: ClaimTime));

        var sum = 0L;
        var least = AllocationWindow.Least(window: () => {
            for (var iteration = 0; (iteration < 512); iteration++) {
                _ = arena.TryReadLiveRaw(fieldOrdinal: ((iteration & 1) * 4), handle: handle, raw: out var raw, time: OneSecondLater);
                sum += raw;
            }
        });

        Assert.Equal(
            actual: least,
            expected: 0L
        );
        Assert.NotEqual(
            actual: sum,
            expected: 0L
        );
    }
}
