using Puck.Abstractions.Counting;
using Xunit;
using System.Text.Json;

namespace Puck.State.Tests;

/// <summary>Deterministic allocation, lifetime identity, persistence, and atomic rollback laws for state pools.</summary>
public sealed class StatePoolLawTests {
    [Fact]
    public void EnumDefaultOutsideMembersRefusesBeforeAnyClaim() {
        var section = Section();
        var domain = new StateEnum(CellName.Parse(candidate: "Facing"), [CellName.Parse(candidate: "North"), CellName.Parse(candidate: "South")]);

        section = section with { Enums = [domain], Records = [section.Records![0] with { Fields = [new StatePoolField(CellName.Parse(candidate: "facing"), Enum: domain.Name, Default: CellValue.Int(value: 2))] }] };
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: section));

        Assert.Contains("record 'actor' field 'facing'", refusal.Message);
        Assert.Contains("outside enum 'Facing'", refusal.Message);
    }
    [Fact]
    public void APoolCannotHideAnAuthoredRowOfTheSameName() {
        var section = Section() with { Rows = [new StateRow(CellName.Parse(candidate: "actors"), CellKind.Int)] };
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: section));

        Assert.Contains("pool 'actors' collides", refusal.Message);
    }

    private static StateSection Section(IReadOnlyList<StatePoolSeed>? initial = null, StatePoolSnapshot? snapshot = null) => new(
        Records: [new StateRecord(
            Name: CellName.Parse(candidate: "actor"),
            Fields: [
                new StatePoolField(Name: CellName.Parse(candidate: "score"), Default: CellValue.Int(value: 7), Min: 0L),
                new StatePoolField(Name: CellName.Parse(candidate: "ready"), Kind: CellKind.Bool, Default: CellValue.Bool(value: false)),
            ]
        )],
        Pools: [new StatePool(
            Name: CellName.Parse(candidate: "actors"),
            Record: CellName.Parse(candidate: "actor"),
            Capacity: 4,
            Initial: initial,
            Snapshot: snapshot
        )]
    );
    private static StateArena Build(StateSection section) => new(
        catalog: StateCatalog.Compile(section: section),
        section: section,
        time: ArenaTime.Origin
    );

    [Fact]
    public void CatalogExpandsPoolRowsAndPreinternsTheWholeIdentityUniverse() {
        var catalog = StateCatalog.Compile(section: Section());
        var pool = Assert.Single(collection: catalog.Pools);

        Assert.Equal(expected: 4, actual: catalog.Rows.Count);
        Assert.Equal(expected: 2, actual: pool.Fields.Count);
        Assert.All(catalog.Rows, row => Assert.True(condition: row.Generated));
        for (var slot = 0; (slot < pool.Capacity); slot++) {
            Assert.True(condition: catalog.Keys.TryResolve(
                name: CellName.Parse(candidate: slot.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
                key: out _
            ));
        }
    }
    [Fact]
    public void ClaimReleaseReclaimUsesLowestSlotAndRejectsAStaleLifetime() {
        var arena = Build(section: Section());

        Assert.True(condition: arena.TryClaim(handle: out var first, poolOrdinal: 0, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: (0, 0, 0L), actual: (first.PoolOrdinal, first.Slot, first.Generation));
        Assert.True(condition: arena.TryWrite(handle: first, fieldOrdinal: 0, value: CellValue.Int(value: 11), reason: out reason), userMessage: reason);
        Assert.True(condition: arena.TryRelease(handle: first, reason: out reason), userMessage: reason);
        Assert.False(condition: arena.TryResolve(handle: first, position: out _));
        Assert.False(condition: arena.TryWrite(handle: first, fieldOrdinal: 0, value: CellValue.Int(value: 12), reason: out _));

        Assert.True(condition: arena.TryClaim(handle: out var second, poolOrdinal: 0, reason: out reason), userMessage: reason);
        Assert.Equal(expected: (0, 0, 1L), actual: (second.PoolOrdinal, second.Slot, second.Generation));
        Assert.True(condition: arena.TryResolvePoolSlot(handle: out var current, poolOrdinal: 0, slot: 0));
        Assert.Equal(actual: current, expected: second);
        Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: second, value: out var score));
        Assert.Equal(expected: 7L, actual: score.AsInt);
    }
    [Fact]
    public void RewindRestoresPoolOccupancyPositionAndGeneration() {
        var arena = Build(section: Section(initial: [new StatePoolSeed(Slot: 0), new StatePoolSeed(Slot: 2)]));
        var original = arena.SnapshotPool(poolOrdinal: 0).ToArray();
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryRelease(handle: original[0], reason: out var reason), userMessage: reason);
        Assert.True(condition: arena.TryClaim(handle: out var replacement, poolOrdinal: 0, reason: out reason), userMessage: reason);
        Assert.Equal(expected: 0, actual: replacement.Slot);
        Assert.Equal(expected: 1L, actual: replacement.Generation);
        Assert.True(condition: arena.TryClaim(handle: out var inserted, poolOrdinal: 0, reason: out reason), userMessage: reason);
        Assert.Equal(expected: 1, actual: inserted.Slot);

        arena.Rewind(mark: mark);

        Assert.Equal(expected: original, actual: arena.SnapshotPool(poolOrdinal: 0).ToArray());
        Assert.True(condition: arena.TryResolve(handle: original[0], position: out var firstPosition));
        Assert.Equal(actual: firstPosition, expected: 0);
        Assert.True(condition: arena.TryResolve(handle: original[1], position: out var lastPosition));
        Assert.Equal(actual: lastPosition, expected: 2);
        Assert.False(condition: arena.TryResolve(handle: replacement, position: out _));
        Assert.False(condition: arena.TryResolve(handle: inserted, position: out _));
        Assert.True(condition: arena.TryClaim(handle: out var afterRewind, poolOrdinal: 0, reason: out reason), userMessage: reason);
        Assert.Equal(expected: 1, actual: afterRewind.Slot);
        Assert.Equal(expected: 0L, actual: afterRewind.Generation);
    }
    [Fact]
    public void MiddleReleasePreservesPositionsAndReclaimRestoresPresence() {
        var arena = Build(section: Section());

        Assert.True(condition: arena.TryClaim(handle: out var first, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaim(handle: out var middle, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaim(handle: out var last, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryResolve(handle: first, position: out var firstBefore));
        Assert.True(condition: arena.TryResolve(handle: middle, position: out var middleBefore));
        Assert.True(condition: arena.TryResolve(handle: last, position: out var lastBefore));
        Assert.Equal(actual: (firstBefore, middleBefore, lastBefore), expected: (0, 1, 2));

        Assert.True(condition: arena.TryRelease(handle: middle, reason: out _));
        Assert.True(condition: arena.TryResolve(handle: first, position: out var firstAfterRelease));
        Assert.True(condition: arena.TryResolve(handle: last, position: out var lastAfterRelease));
        Assert.Equal(actual: (firstAfterRelease, lastAfterRelease), expected: (0, 2));
        Assert.True(condition: arena.TryClaim(handle: out var replacement, poolOrdinal: 0, reason: out _));
        Assert.Equal(expected: (middle.Slot, 1L), actual: (replacement.Slot, replacement.Generation));
        Assert.True(condition: arena.TryResolve(handle: replacement, position: out var replacementPosition));
        Assert.True(condition: arena.TryResolve(handle: last, position: out var lastAfterReclaim));
        Assert.Equal(actual: (replacementPosition, lastAfterReclaim), expected: (1, 2));
    }
    [Fact]
    public void OuterRewindRestoresLiveMembershipFieldsAndDeadGeneration() {
        var arena = Build(section: Section());

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out var reason), userMessage: reason);
        Assert.True(condition: arena.TryWrite(handle: handle, fieldOrdinal: 0, value: CellValue.Int(value: 17), reason: out reason), userMessage: reason);
        var before = arena.ComputeHash();
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryRelease(handle: handle, reason: out reason), userMessage: reason);
        Assert.True(condition: arena.TryClaim(handle: out var replacement, poolOrdinal: 0, reason: out reason), userMessage: reason);
        Assert.Equal(expected: 1L, actual: replacement.Generation);

        arena.Rewind(mark: mark);

        Assert.Equal(expected: before, actual: arena.ComputeHash());
        Assert.True(condition: arena.TryResolve(handle: handle, position: out _));
        Assert.False(condition: arena.TryResolve(handle: replacement, position: out _));
        Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: handle, value: out var score));
        Assert.Equal(expected: 17L, actual: score.AsInt);
    }
    [Fact]
    public void SnapshotRoundTripPreservesAllocatorContinuationAndHash() {
        var source = Build(section: Section());

        Assert.True(condition: source.TryClaim(handle: out var first, poolOrdinal: 0, reason: out var reason), userMessage: reason);
        Assert.True(condition: source.TryClaim(handle: out var second, poolOrdinal: 0, reason: out reason), userMessage: reason);
        Assert.True(condition: source.TryWrite(handle: second, fieldOrdinal: 0, value: CellValue.Int(value: 23), reason: out reason), userMessage: reason);
        Assert.True(condition: source.TryRelease(handle: first, reason: out reason), userMessage: reason);

        var exported = Assert.Single(collection: source.ToPools());
        var restoredSection = Section(snapshot: exported.Snapshot);
        var restored = Build(section: restoredSection);

        Assert.Equal(expected: source.ComputeHash(), actual: restored.ComputeHash());
        Assert.Equal(
            expected: source.SnapshotPool(poolOrdinal: 0).Select(selector: static handle => (handle.PoolOrdinal, handle.Slot, handle.Generation)),
            actual: restored.SnapshotPool(poolOrdinal: 0).Select(selector: static handle => (handle.PoolOrdinal, handle.Slot, handle.Generation)));
        Assert.True(condition: restored.TryClaim(handle: out var reclaimed, poolOrdinal: 0, reason: out reason), userMessage: reason);
        Assert.Equal(expected: (0, 0, 1L), actual: (reclaimed.PoolOrdinal, reclaimed.Slot, reclaimed.Generation));
    }
    [Fact]
    public void GenericMutationCannotBreakGeneratedPoolRows() {
        var arena = Build(section: Section());
        var pool = arena.Catalog.Pools[0];

        Assert.True(condition: arena.Keys.TryResolve(name: CellName.Parse(candidate: "0"), key: out var key));

        Assert.False(condition: arena.TryRemove(rowOrdinal: pool.GenerationRowOrdinal, key: key, reason: out var removeReason));
        Assert.Contains(actualString: removeReason, expectedSubstring: "owned by a state pool");
        Assert.False(condition: arena.TryWrite(rowOrdinal: pool.GenerationRowOrdinal, key: key, value: CellValue.Int(value: 4), reason: out var writeReason));
        Assert.Contains(actualString: writeReason, expectedSubstring: "owned by a state pool");
        Assert.False(condition: arena.TryWriteBehavior(rowOrdinal: pool.GenerationRowOrdinal, key: key, behavior: StateCellBehavior.None));
        Assert.False(condition: arena.TryWriteObservationAt(rowOrdinal: pool.GenerationRowOrdinal, position: 0, observation: null));
        Assert.False(condition: arena.TryReorder(rowOrdinal: pool.GenerationRowOrdinal, order: [0, 1, 2, 3], reason: out var reorderReason));
        Assert.Contains(actualString: reorderReason, expectedSubstring: "owned by a state pool");
    }
    [Fact]
    public void SnapshotRejectsUndeclaredAndDuplicateFieldOverrides() {
        var undeclared = Section(initial: [new StatePoolSeed(
            Slot: 0,
            Values: [new StatePoolValue(Field: CellName.Parse(candidate: "missing"), Value: CellValue.Int(value: 1))]
        )]);
        var duplicate = Section(initial: [new StatePoolSeed(
            Slot: 0,
            Values: [
                new StatePoolValue(Field: CellName.Parse(candidate: "score"), Value: CellValue.Int(value: 1)),
                new StatePoolValue(Field: CellName.Parse(candidate: "score"), Value: CellValue.Int(value: 2)),
            ]
        )]);

        Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: undeclared));
        Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: duplicate));
    }
    [Fact]
    public void MalformedUnusedPoolDeclarationsRefuseByNameBeforeExpansion() {
        var record = new StateRecord(Name: CellName.Parse(candidate: "unused"), Fields: [
            new StatePoolField(Name: CellName.Parse(candidate: "score"), Kind: CellKind.Int, Default: CellValue.Text(value: "wrong")),
        ]);
        var badDefault = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(Records: [record])));

        Assert.Contains(expectedSubstring: "record 'unused' field 'score'", actualString: badDefault.Message);
        Assert.Contains(expectedSubstring: "default", actualString: badDefault.Message);

        var nullPool = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(Pools: [null!])));

        Assert.Contains(expectedSubstring: "null pool declaration", actualString: nullPool.Message);

        var nullPair = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(PairPools: [null!])));

        Assert.Contains(expectedSubstring: "null pair pool declaration", actualString: nullPair.Message);

        var nullRow = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(Rows: [null!])));

        Assert.Contains(expectedSubstring: "null declaration at ordinal 0", actualString: nullRow.Message);

        foreach (var dimensions in new[] { -1, (StateCapacity.MaxVectorDimensions + 1) }) {
            var vectorRecord = new StateRecord(Name: CellName.Parse(candidate: "unusedVector"), Fields: [
                new StatePoolField(Name: CellName.Parse(candidate: "pose"), Kind: CellKind.Vector, Dimensions: dimensions),
            ]);
            var badDimensions = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(Records: [vectorRecord])));

            Assert.Contains(expectedSubstring: "record 'unusedVector' field 'pose'", actualString: badDimensions.Message);
            Assert.Contains(expectedSubstring: "vector dimensions", actualString: badDimensions.Message);
        }

        var boundedRecord = new StateRecord(Name: CellName.Parse(candidate: "bounded"), Fields: [
            new StatePoolField(Name: CellName.Parse(candidate: "score"), Default: CellValue.Int(value: 11), Min: 0, Max: 10, Overflow: StateOverflow.Saturate),
        ]);
        var badBoundedDefault = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(Records: [boundedRecord])));

        Assert.Contains(expectedSubstring: "record 'bounded' field 'score' default 11", actualString: badBoundedDefault.Message);
        Assert.Contains(expectedSubstring: "0..10 envelope", actualString: badBoundedDefault.Message);
    }
    [Fact]
    public void PoolKeyResolutionAboveRuntimeStringCacheIsAllocationFreeWhenWarm() {
        var section = new StateSection(
            Records: [new StateRecord(Name: CellName.Parse(candidate: "actor"))],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "actors"), Record: CellName.Parse(candidate: "actor"), Capacity: 512)]);
        var arena = Build(section: section);

        Assert.True(condition: arena.TryPoolKey(key: out var expected, slot: 300));

        var resolved = true;
        var actual = default(CellKey);

        var before = AllocationWindow.Least(window: () => {
            for (var iteration = 0; (iteration < 256); iteration++) {
                resolved &= arena.TryPoolKey(key: out actual, slot: 300);
            }
        });

        var allocated = before;

        Assert.True(condition: resolved);
        Assert.Equal(actual: actual, expected: expected);
        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void AdvancingFieldBirthsAtClaimAndPreservesItsClockAcrossPublication() {
        var field = new StatePoolField(
            Name: CellName.Parse(candidate: "score"),
            Default: CellValue.Int(value: 7L),
            Advance: new StateAdvance(PerSecondDenominator: 1L, PerSecondNumerator: 1L));
        var section = new StateSection(
            Records: [new StateRecord(Name: CellName.Parse(candidate: "actor"), Fields: [field])],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "actors"), Record: CellName.Parse(candidate: "actor"), Capacity: 1)]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, section: section, time: ArenaTime.Origin);
        var claimTime = ArenaTime.At(engineTick: 50_400UL, tick: 3UL);

        Assert.True(condition: arena.TryClaim(handle: out var first, poolOrdinal: 0, reason: out var reason, time: claimTime), userMessage: reason);
        var oneSecondLater = claimTime with { EngineTick = 100_800UL };

        Assert.True(condition: arena.TryReadLive(fieldOrdinal: 0, handle: first, time: oneSecondLater, value: out var advanced));
        Assert.Equal(expected: 8L, actual: advanced.AsInt);
        Assert.True(condition: arena.TryWriteLive(fieldOrdinal: 0, handle: first, operand: 2L, reason: out reason, time: oneSecondLater, write: StateWriteKind.Add), userMessage: reason);

        var published = Assert.Single(collection: arena.ToPools());
        var restoredSection = section with { Pools = [published] };
        var restored = new StateArena(catalog: StateCatalog.Compile(section: restoredSection), section: restoredSection, time: oneSecondLater);
        var restoredHandle = Assert.Single(collection: restored.SnapshotPool(poolOrdinal: 0));

        Assert.True(condition: restored.TryReadLive(fieldOrdinal: 0, handle: restoredHandle, time: oneSecondLater, value: out var restoredValue));
        Assert.Equal(expected: 10L, actual: restoredValue.AsInt);

        Assert.True(condition: restored.TryRelease(handle: restoredHandle, reason: out reason), userMessage: reason);
        var reclaimTime = oneSecondLater with { EngineTick = 151_200UL };

        Assert.True(condition: restored.TryClaim(handle: out var replacement, poolOrdinal: 0, reason: out reason, time: reclaimTime), userMessage: reason);
        Assert.Equal(expected: 1L, actual: replacement.Generation);
        Assert.True(condition: restored.TryReadLive(fieldOrdinal: 0, handle: replacement, time: reclaimTime, value: out var fresh));
        Assert.Equal(expected: 7L, actual: fresh.AsInt);
    }
    [Fact]
    public void CompactingAPoolPreservesAnImportedClockWithoutAnAdvanceTrait() {
        var clock = new StateCellClock(EpochEngineTick: 40L, EpochTick: 3L, SubstepTicks: 2L, V0: 7L, Y0: 11L);
        var section = Section(snapshot: new StatePoolSnapshot(
            Generations: [0L, 0L, 0L, 0L],
            Live: [
                new StatePoolSeed(Slot: 0, Values: [new StatePoolValue(Field: CellName.Parse(candidate: "score"), Value: CellValue.Int(value: 7L)), new StatePoolValue(Field: CellName.Parse(candidate: "ready"), Value: CellValue.Bool(value: false))]),
                new StatePoolSeed(Slot: 1, Values: [new StatePoolValue(Field: CellName.Parse(candidate: "score"), Value: CellValue.Int(value: 8L)), new StatePoolValue(Field: CellName.Parse(candidate: "ready"), Value: CellValue.Bool(value: false))]),
                new StatePoolSeed(Slot: 2, Values: [new StatePoolValue(Field: CellName.Parse(candidate: "score"), Value: CellValue.Int(value: 9L), Clock: clock), new StatePoolValue(Field: CellName.Parse(candidate: "ready"), Value: CellValue.Bool(value: false))]),
            ]
        ));
        var arena = Build(section: section);
        var handles = arena.SnapshotPool(poolOrdinal: 0);
        var pool = arena.Catalog.Pools[0];
        var rows = pool.Fields.Select(selector: static field => field.RowOrdinal).Append(element: pool.DomainRowOrdinal).Append(element: pool.GenerationRowOrdinal).Order().ToArray();

        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 1, Name: "turn", Rows: rows)]);
        var before = arena.ComputeHash();

        arena.BeginUndoTurn(group: "turn");
        arena.BeginUndoPass(group: "turn");
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryRelease(handle: handles[1], reason: out var reason), userMessage: reason);
        arena.Commit(mark: mark);
        arena.EndUndoPass(group: "turn");
        arena.CommitUndoTurn(group: "turn");

        var exported = Assert.Single(collection: arena.ToPools());
        var moved = Assert.Single(collection: exported.Snapshot!.Live!, predicate: static seed => (seed.Slot == 2));
        var score = Assert.Single(collection: moved.Values!, predicate: static value => (value.Field.Value == "score"));

        Assert.Equal(expected: clock, actual: score.Clock);
        Assert.Equal(expected: 9L, actual: score.Value.AsInt);
        Assert.True(condition: arena.TryRewindGroup(group: "turn", reason: out reason), userMessage: reason);
        Assert.Equal(expected: before, actual: arena.ComputeHash());
    }
    [Fact]
    public void ReleasingAnotherSlotPreservesAnInactiveClockRestoredFromAnUndoSnapshot() {
        StateArena RestoreClockAt(int position) {
            var arena = Build(section: Section(initial: [new StatePoolSeed(Slot: 0), new StatePoolSeed(Slot: 1), new StatePoolSeed(Slot: 2)]));
            var fieldRow = arena.Catalog.Pools[0].Fields[0].RowOrdinal;
            var slot = (arena.Layout[fieldRow].CellStart + position);
            var entries = new[] {
                new ArenaUndoEntrySnapshot(Column: ArenaColumn.ClockEpochTick, Components: null, Index: slot, MemberKey: null, Number: 3L, Observation: null, Text: null, Visibility: null),
                new ArenaUndoEntrySnapshot(Column: ArenaColumn.ClockEpochEngineTick, Components: null, Index: slot, MemberKey: null, Number: 5L, Observation: null, Text: null, Visibility: null),
                new ArenaUndoEntrySnapshot(Column: ArenaColumn.ClockY0, Components: null, Index: slot, MemberKey: null, Number: 7L, Observation: null, Text: null, Visibility: null),
                new ArenaUndoEntrySnapshot(Column: ArenaColumn.ClockV0, Components: null, Index: slot, MemberKey: null, Number: 11L, Observation: null, Text: null, Visibility: null),
                new ArenaUndoEntrySnapshot(Column: ArenaColumn.ClockSubstepTicks, Components: null, Index: slot, MemberKey: null, Number: 13L, Observation: null, Text: null, Visibility: null),
            };

            arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 1, Name: "turn", Rows: [fieldRow])]);
            var snapshot = new ArenaUndoSnapshot(Groups: [new ArenaUndoGroupSnapshot(Name: "turn", Depth: 1, Rows: [fieldRow], Segments: [new ArenaUndoSegmentSnapshot(Entries: entries, Rewindable: true)], Pending: null)]);

            Assert.True(condition: arena.TryImportUndoSnapshot(reason: out var reason, snapshot: snapshot), userMessage: reason);
            Assert.True(condition: arena.TryRewindGroup(group: "turn", reason: out reason), userMessage: reason);
            return arena;
        }

        var released = RestoreClockAt(position: 2);
        var releasedHandles = released.SnapshotPool(poolOrdinal: 0);

        Assert.True(condition: released.TryRelease(handle: releasedHandles[1], reason: out var reason), userMessage: reason);

        var control = RestoreClockAt(position: 2);
        var controlHandles = control.SnapshotPool(poolOrdinal: 0);

        Assert.True(condition: control.TryRelease(handle: controlHandles[0], reason: out reason), userMessage: reason);
        foreach (var column in new[] {
            ArenaColumn.ClockSet,
            ArenaColumn.ClockEpochTick,
            ArenaColumn.ClockEpochEngineTick,
            ArenaColumn.ClockY0,
            ArenaColumn.ClockV0,
            ArenaColumn.ClockSubstepTicks,
        }) {
            Assert.Equal(expected: control.ComputeColumnHash(column: column), actual: released.ComputeColumnHash(column: column));
        }
    }
    [Fact]
    public void AdvancingFieldRequiresNumericKindAndPositiveDenominator() {
        foreach (var field in new[] {
            new StatePoolField(Name: CellName.Parse(candidate: "bad"), Kind: CellKind.Bool, Default: CellValue.Bool(value: false), Advance: new StateAdvance(PerSecondDenominator: 1L, PerSecondNumerator: 1L)),
            new StatePoolField(Name: CellName.Parse(candidate: "bad"), Advance: new StateAdvance(PerSecondDenominator: 0L, PerSecondNumerator: 1L)),
        }) {
            Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(Records: [new StateRecord(Name: CellName.Parse(candidate: "record"), Fields: [field])])));
        }
    }
    [Fact]
    public void PairClaimCanonicalizesAndEndpointReleaseCascadesThroughRewind() {
        var section = new StateSection(
            Records: [
                new StateRecord(Name: CellName.Parse(candidate: "node")),
                new StateRecord(Name: CellName.Parse(candidate: "edge"), Fields: [new StatePoolField(Name: CellName.Parse(candidate: "weight"), Default: CellValue.Int(value: 3))]),
            ],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "nodes"), Record: CellName.Parse(candidate: "node"), Capacity: 3)],
            PairPools: [new StatePairPool(Name: CellName.Parse(candidate: "edges"), Record: CellName.Parse(candidate: "edge"), LeftPool: CellName.Parse(candidate: "nodes"), RightPool: CellName.Parse(candidate: "nodes"), MaxLive: 2, Directed: false)]);
        var arena = Build(section: section);

        Assert.True(condition: arena.TryClaim(handle: out var a, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaim(handle: out var b, poolOrdinal: 0, reason: out _));
        Assert.False(condition: arena.TryClaim(handle: out _, poolOrdinal: 1, reason: out var genericReason));
        Assert.Contains(actualString: genericReason, expectedSubstring: "TryClaimPair");
        Assert.True(condition: arena.TryClaimPair(handle: out var pair, leftHandle: b, poolOrdinal: 1, reason: out _, rightHandle: a));
        Assert.Equal(expected: 1, actual: pair.Slot);
        Assert.False(condition: arena.TryClaimPair(handle: out _, leftHandle: a, poolOrdinal: 1, reason: out _, rightHandle: b));

        var before = arena.ComputeHash();
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryRelease(handle: a, reason: out _));
        Assert.False(condition: arena.TryResolve(handle: pair, position: out _));
        arena.Rewind(mark: mark);
        Assert.Equal(expected: before, actual: arena.ComputeHash());
        Assert.True(condition: arena.TryResolve(handle: pair, position: out _));
    }
    [Fact]
    public void PairSnapshotRejectsDeadEndpointsAndVectorDefaultsOwnTheirPayload() {
        var components = new sbyte[] { 4, 5 };
        var field = new StatePoolField(Name: CellName.Parse(candidate: "v"), Kind: CellKind.Vector, Default: CellValue.Vector(components: components), Dimensions: 2);

        components[0] = 99;
        Assert.Equal(expected: ((sbyte)4), actual: field.Default.AsVector.Span[0]);

        var section = new StateSection(
            Records: [new StateRecord(Name: CellName.Parse(candidate: "node")), new StateRecord(Name: CellName.Parse(candidate: "edge"))],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "nodes"), Record: CellName.Parse(candidate: "node"), Capacity: 2)],
            PairPools: [new StatePairPool(Name: CellName.Parse(candidate: "edges"), Record: CellName.Parse(candidate: "edge"), LeftPool: CellName.Parse(candidate: "nodes"), RightPool: CellName.Parse(candidate: "nodes"), MaxLive: 1, Snapshot: new StatePoolSnapshot(Generations: new long[4], Live: [new StatePoolSeed(Slot: 1)]))]);

        Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: section));
    }
    [Fact]
    public void PairBoundsAndUndirectedEndpointShapeAreValidated() {
        StateSection Pair(bool directed, CellName right, int maxLive) => new(
            Records: [new StateRecord(Name: CellName.Parse(candidate: "r"))],
            Pools: [
                new StatePool(Name: CellName.Parse(candidate: "a"), Record: CellName.Parse(candidate: "r"), Capacity: 2),
                new StatePool(Name: CellName.Parse(candidate: "b"), Record: CellName.Parse(candidate: "r"), Capacity: 2),
            ],
            PairPools: [new StatePairPool(Name: CellName.Parse(candidate: "p"), Record: CellName.Parse(candidate: "r"), LeftPool: CellName.Parse(candidate: "a"), RightPool: right, MaxLive: maxLive, Directed: directed)]);
        Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: Pair(directed: false, right: CellName.Parse(candidate: "b"), maxLive: 1)));
        Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: Pair(directed: true, right: CellName.Parse(candidate: "b"), maxLive: 5)));

        var cycle = new StateSection(
            Records: [new StateRecord(Name: CellName.Parse(candidate: "r"))],
            PairPools: [
                new StatePairPool(Name: CellName.Parse(candidate: "x"), Record: CellName.Parse(candidate: "r"), LeftPool: CellName.Parse(candidate: "y"), RightPool: CellName.Parse(candidate: "y"), MaxLive: 1),
                new StatePairPool(Name: CellName.Parse(candidate: "y"), Record: CellName.Parse(candidate: "r"), LeftPool: CellName.Parse(candidate: "x"), RightPool: CellName.Parse(candidate: "x"), MaxLive: 1),
            ]);
        var error = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: cycle));

        Assert.Contains(expectedSubstring: "cycle", actualString: error.Message);
    }
    [Fact]
    public void AcyclicPairDependenciesCascadeTransitively() {
        var r = CellName.Parse(candidate: "r");
        var section = new StateSection(
            Records: [new StateRecord(Name: r)],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "nodes"), Record: r, Capacity: 2)],
            PairPools: [
                new StatePairPool(Name: CellName.Parse(candidate: "edges"), Record: r, LeftPool: CellName.Parse(candidate: "nodes"), RightPool: CellName.Parse(candidate: "nodes"), MaxLive: 2),
                new StatePairPool(Name: CellName.Parse(candidate: "labels"), Record: r, LeftPool: CellName.Parse(candidate: "edges"), RightPool: CellName.Parse(candidate: "nodes"), MaxLive: 2),
            ]);
        var arena = Build(section: section);

        Assert.True(condition: arena.TryClaim(handle: out var a, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaim(handle: out var b, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaimPair(handle: out var edge, leftHandle: a, poolOrdinal: 1, reason: out _, rightHandle: b));
        Assert.True(condition: arena.TryClaimPair(handle: out var label, leftHandle: edge, poolOrdinal: 2, reason: out _, rightHandle: a));

        Assert.True(condition: arena.TryRelease(handle: b, reason: out _));
        Assert.False(condition: arena.TryResolve(handle: edge, position: out _));
        Assert.False(condition: arena.TryResolve(handle: label, position: out _));
    }
    [Fact]
    public void CellValueWireShapeRoundTripsEveryCaseAndRefusesUnknownMembers() {
        CellValue[] values = [default, CellValue.Int(value: 7), CellValue.Fixed(rawBits: -3), CellValue.Bool(value: true), CellValue.Text(value: "x"), CellValue.Vector(components: new sbyte[] { -1, 2 })];

        foreach (var value in values) {
            var json = JsonSerializer.Serialize(value: value);

            Assert.Equal(expected: value, actual: JsonSerializer.Deserialize<CellValue>(json: json));
        }
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<CellValue>(json: "{\"kind\":\"Int\",\"value\":1,\"extra\":2}"));
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<CellValue>(json: "{\"kind\":\"Int\",\"value\":true}"));
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<CellValue>(json: "{\"kind\":\"Vector\",\"value\":[128]}"));
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<CellValue>(json: "{\"kind\":\"Vector\",\"value\":[true]}"));
        var oversizedText = new string(c: 'x', count: (StateCapacity.MaxTextValueLength + 1));

        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<CellValue>(json: $"{{\"kind\":\"Text\",\"value\":{JsonSerializer.Serialize(value: oversizedText)}}}"));
    }
    [Fact]
    public void SnapshotRequiresEveryFieldAndGenericImportCannotReplacePoolRows() {
        var incomplete = Section(snapshot: new StatePoolSnapshot(Generations: new long[4], Live: [new StatePoolSeed(Slot: 0, Values: [new StatePoolValue(Field: CellName.Parse(candidate: "score"), Value: CellValue.Int(value: 1))])]));

        Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: incomplete));

        var section = Section(initial: [new StatePoolSeed(Slot: 0)]);
        var arena = Build(section: section);
        var row = arena.Catalog.Rows[arena.Catalog.Pools[0].DomainRowOrdinal] with { Cells = [] };

        Assert.False(condition: arena.TryLoad(rows: [row], time: ArenaTime.Origin, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "owned by a state pool");
        Assert.Single(collection: arena.SnapshotPool(poolOrdinal: 0));
    }
    [Fact]
    public void RelayoutCarriesPoolAndPairContinuationAndRefusesNarrowingAtomically() {
        static StateSection PairSection(int capacity) => new(
            Records: [new StateRecord(Name: CellName.Parse(candidate: "node")), new StateRecord(Name: CellName.Parse(candidate: "edge"))],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "nodes"), Record: CellName.Parse(candidate: "node"), Capacity: capacity)],
            PairPools: [new StatePairPool(Name: CellName.Parse(candidate: "edges"), Record: CellName.Parse(candidate: "edge"), LeftPool: CellName.Parse(candidate: "nodes"), RightPool: CellName.Parse(candidate: "nodes"), MaxLive: (capacity * capacity))]);

        var section = PairSection(capacity: 3);
        var arena = Build(section: section);

        Assert.True(condition: arena.TryClaim(handle: out var first, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaim(handle: out var second, poolOrdinal: 0, reason: out _));
        Assert.True(condition: arena.TryClaimPair(handle: out var pair, leftHandle: first, poolOrdinal: 1, reason: out _, rightHandle: second));
        Assert.True(condition: arena.TryRelease(handle: pair, reason: out _));
        Assert.True(condition: arena.TryClaimPair(handle: out var replacement, leftHandle: first, poolOrdinal: 1, reason: out _, rightHandle: second));
        Assert.Equal(expected: 1L, actual: replacement.Generation);

        var before = arena.ComputeHash();

        Assert.True(arena.TryRelayout(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: before, actual: arena.ComputeHash());
        Assert.False(condition: arena.TryResolve(handle: replacement, position: out _));
        var rebound = Assert.Single(collection: arena.SnapshotPool(poolOrdinal: 1));

        Assert.Equal(expected: replacement.Generation, actual: rebound.Generation);

        var narrowed = PairSection(capacity: 1);
        var stable = arena.ComputeHash();

        Assert.False(condition: arena.TryRelayout(catalog: StateCatalog.Compile(section: narrowed), section: narrowed, time: ArenaTime.Origin, reason: out _));
        Assert.Equal(expected: stable, actual: arena.ComputeHash());
        Assert.True(condition: arena.TryResolve(handle: rebound, position: out _));
    }
    [Fact]
    public void GenerationExhaustionRefusesReleaseAtomically() {
        var section = Section(snapshot: new StatePoolSnapshot(
            Generations: [long.MaxValue, 0L, 0L, 0L],
            Live: [new StatePoolSeed(Slot: 0, Values: [
                new StatePoolValue(Field: CellName.Parse(candidate: "score"), Value: CellValue.Int(value: 9)),
                new StatePoolValue(Field: CellName.Parse(candidate: "ready"), Value: CellValue.Bool(value: true)),
            ])]));
        var arena = Build(section: section);
        var handle = Assert.Single(collection: arena.SnapshotPool(poolOrdinal: 0));
        var before = arena.ComputeHash();

        Assert.False(condition: arena.TryRelease(handle: handle, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "exhausted");
        Assert.Equal(expected: before, actual: arena.ComputeHash());
        Assert.True(condition: arena.TryResolve(handle: handle, position: out _));
    }
    [Fact]
    public void HandlesAreBoundToTheirCatalogContext() {
        var first = Build(section: Section(initial: [new StatePoolSeed(Slot: 0)]));
        var second = Build(section: Section(initial: [new StatePoolSeed(Slot: 0)]));
        var firstHandle = Assert.Single(collection: first.SnapshotPool(poolOrdinal: 0));

        Assert.False(condition: second.TryResolve(handle: firstHandle, position: out _));
        Assert.False(condition: second.TryResolve(handle: default, position: out _));
        Assert.True(condition: second.TryResolve(handle: second.Catalog.CreateInstanceHandle(generation: 0L, poolOrdinal: 0, slot: 0), position: out _));
    }
}
