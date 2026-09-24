using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a pool field's number read answers, at the read's time, exactly the number its
/// carrier read holds — an advancing field advanced, a fixed field's bits, a boolean's zero or one — refuses exactly
/// where the carrier read refuses and on every text or vector field, follows a relayout's field order, and allocates
/// nothing.</summary>
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

    [InlineData(0, false, 7L)]
    [InlineData(0, true, 7L)]
    [InlineData(1, true, 98_304L)]
    [InlineData(2, true, 1L)]
    [InlineData(4, false, 0L)]
    [InlineData(4, true, 1L)]
    [Theory]
    public void ANumericFieldReadsWhatItsCarrierHoldsAtTheReadsTime(int fieldOrdinal, bool later, long expected) {
        var arena = Build();
        var time = (later ? OneSecondLater : ClaimTime);

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out var reason, time: ClaimTime), userMessage: reason);
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: fieldOrdinal, handle: handle, raw: out var raw, time: time));
        Assert.True(condition: arena.TryReadLive(fieldOrdinal: fieldOrdinal, handle: handle, time: time, value: out var carried));
        Assert.Equal(actual: raw, expected: expected);
        Assert.Equal(expected: carried.Raw, actual: raw);
    }
    [Fact]
    public void ANumberReadRefusesWhereTheCarrierReadDoesAndOnATextField() {
        var arena = Build();

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out _));
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 3, handle: handle, raw: out var text, time: OneSecondLater));
        Assert.Equal(actual: text, expected: 0L);
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 5, handle: handle, raw: out _, time: OneSecondLater));
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: -1, handle: handle, raw: out _, time: OneSecondLater));
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: default, raw: out _, time: OneSecondLater));
        Assert.True(condition: arena.TryRelease(handle: handle, reason: out _));
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: handle, raw: out _, time: OneSecondLater));
        Assert.True(condition: arena.TryClaim(handle: out var replacement, poolOrdinal: 0, reason: out _));
        Assert.Equal(expected: handle.Slot, actual: replacement.Slot);
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: handle, raw: out _, time: OneSecondLater));
        Assert.True(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: replacement, raw: out _, time: OneSecondLater));
    }
    [Fact]
    public void ANumberReadAddressesEverySlotAcrossAWordBoundaryAndSkipsAReleasedOne() {
        var arena = Build();
        var handles = new StateInstanceHandle[130];

        for (var slot = 0; (slot < handles.Length); slot++) {
            Assert.True(condition: arena.TryClaim(handle: out handles[slot], poolOrdinal: 0, reason: out _));
            Assert.True(condition: arena.TryWrite(fieldOrdinal: 0, handle: handles[slot], reason: out _, value: CellValue.Int(value: (1000L + slot))));
        }
        Assert.True(condition: arena.TryRelease(handle: handles[64], reason: out _));
        for (var slot = 0; (slot < handles.Length); slot++) {
            Assert.Equal(
                actual: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: handles[slot], raw: out var raw, time: OneSecondLater),
                expected: (slot != 64)
            );
            Assert.Equal(
                actual: raw,
                expected: ((slot != 64) ? (1000L + slot) : 0L)
            );
        }
    }
    [Fact]
    public void ANumberReadUsesTheRelayoutFieldAddressAfterFieldOrderAndRetainsTraits() {
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

        Assert.Equal(actual: clock, expected: 1L);
        Assert.Equal(actual: count, expected: 7L);
    }
    [Fact]
    public void ANumberReadRefusesVectorFieldsForOrdinaryAndPairInstances() {
        Assert.True(condition: StateVector.TryCreate(components: new sbyte[] { 127, 0, 0, 0, 0, 0, 0, 0 }, error: out var vectorError, vector: out var vector), userMessage: vectorError);
        var record = new StateRecord(Name: Name(value: "item"), Fields: [
            new StatePoolField(Name: Name(value: "direction"), Kind: CellKind.Vector, Space: Name(value: "space8"), Dimensions: 8, Default: CellValue.Vector(components: vector.Memory)),
        ]);
        var section = new StateSection(
            Spaces: [new StateSpace(name: Name(value: "space8"), model: "model", revision: "r1", dimensions: 8)],
            Records: [record],
            Pools: [new StatePool(Name: Name(value: "nodes"), Record: record.Name, Capacity: 2)],
            PairPools: [new StatePairPool(Name: Name(value: "edges"), Record: record.Name, LeftPool: Name(value: "nodes"), RightPool: Name(value: "nodes"), MaxLive: 1)]
        );
        var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);

        Assert.True(condition: arena.TryClaim(handle: out var left, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaim(handle: out var right, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaimPair(handle: out var pair, leftHandle: left, poolOrdinal: 1, reason: out _, rightHandle: right));
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: left, raw: out var ordinary, time: OneSecondLater));
        Assert.False(condition: arena.TryReadLiveRaw(fieldOrdinal: 0, handle: pair, raw: out var paired, time: OneSecondLater));
        Assert.Equal(actual: ordinary, expected: 0L);
        Assert.Equal(actual: paired, expected: 0L);
    }
    [Fact]
    public void AWarmedNumberReadAllocatesNothing() {
        var arena = Build();

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out _, time: ClaimTime));

        var sum = 0L;
        var least = AllocationWindow.Least(window: () => {
            for (var iteration = 0; (iteration < 512); iteration++) {
                _ = arena.TryReadLiveRaw(fieldOrdinal: ((iteration & 1) * 4), handle: handle, raw: out var raw, time: OneSecondLater);
                sum += raw;
            }
        });

        Assert.Equal(actual: least, expected: 0L);
        Assert.NotEqual(actual: sum, expected: 0L);
    }
}
