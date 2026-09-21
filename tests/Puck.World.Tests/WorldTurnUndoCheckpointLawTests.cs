using Puck.State.Rules;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Retained turns survive the authority wire and a rebuilt arena, including an unfinished staged turn.</summary>
public sealed class WorldTurnUndoCheckpointLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [new WorldStateRow(Name: Name(value: "score"), Kind: CellKind.Int,
            Min: 0, Max: 100, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0))])]),
        Rules = [
            new WorldRule(Name: Name(value: "first"), Effects: [new ActionEffect.AddState(State: "score", Value: 1)]),
            new WorldRule(Name: Name(value: "second"), Effects: [new ActionEffect.AddState(State: "score", Value: 2)]),
        ],
        RuleGroupsRaw = [new RuleGroupDeclaration(Name: Name(value: "turn"), Shape: RuleGroupShape.Staged,
            Steps: [new RuleGroupStep(Name(value: "first")), new RuleGroupStep(Name(value: "second"))],
            Undo: new RuleGroupUndo([Name(value: "score")], 8))],
    };
    private static WorldAuthorityCheckpoint Capture(WorldFixture fixture) {
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var captured, hostRow: WorldAuthorityHostRowCheckpoint.Empty, reason: out var reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!), checkpoint: out var decoded, reason: out reason), userMessage: reason);
        return decoded!;
    }
    private static long ReadSlot(WorldFixture fixture, string row) {
        Assert.True(condition: fixture.Server.Arena.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: row));
        Assert.True(condition: fixture.Server.Arena.TryRead(rowOrdinal: handle.Ordinal, key: fixture.Server.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey), value: out var value));
        return value.AsInt;
    }

    [Fact]
    public void CheckpointOfUnfinishedTurnCompletesAndRewindsLikeOriginal() {
        using var original = Fixtures.FreshServer(definition: Document());
        using var restored = Fixtures.FreshServer(definition: Document());

        original.Step();
        var checkpoint = Capture(fixture: original);

        Assert.NotNull(Assert.Single(checkpoint.Server.Undo!.Groups).Pending);
        restored.Server.RestoreCheckpoint(checkpoint: checkpoint);
        Assert.Equal(original.Server.Arena.ComputeHash(), restored.Server.Arena.ComputeHash());
        original.Step();
        restored.Step();
        Assert.Equal(original.Server.Arena.ComputeHash(), restored.Server.Arena.ComputeHash());
        Assert.True(original.Server.Arena.TryRewindTurn("turn", out var reason), reason);
        Assert.True(restored.Server.Arena.TryRewindTurn("turn", out reason), reason);
        Assert.Equal(original.Server.Arena.ComputeHash(), restored.Server.Arena.ComputeHash());
        Assert.False(restored.Server.Arena.TryRewindTurn("turn", out reason));
        Assert.Contains("no retained turn", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void EightTurnsAndWireRestoreRewindToInitialArenaHash() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var before = fixture.Server.Arena.ComputeHash();

        for (var tick = 0; (tick < 16); tick++) { fixture.Step(); }
        var checkpoint = Capture(fixture: fixture);

        Assert.Equal(8, Assert.Single(checkpoint.Server.Undo!.Groups).Segments.Count);
        fixture.Server.RestoreCheckpoint(checkpoint: checkpoint);
        for (var turn = 0; (turn < 8); turn++) {
            Assert.True(fixture.Server.Arena.TryRewindTurn("turn", out var reason), reason);
        }
        Assert.Equal(before, fixture.Server.Arena.ComputeHash());
    }
    [Fact]
    public void InvalidRetainedTurnRefusesBeforeReplacingLiveStateOrClock() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        fixture.Step();
        fixture.Step();
        var checkpoint = Capture(fixture: fixture);
        var group = Assert.Single(checkpoint.Server.Undo!.Groups);
        var segment = Assert.Single(group.Segments);
        var corrupted = segment with {
            Entries = segment.Entries.Select(entry => ((entry.Column == ArenaColumn.Number) ? entry with { Number = 101 } : entry)).ToArray(),
        };

        checkpoint = checkpoint with {
            Server = checkpoint.Server with {
                Undo = new ArenaUndoSnapshot([group with { Segments = [corrupted] }]),
            },
        };
        fixture.Step();
        var before = fixture.Server.Arena.ComputeHash();
        var time = fixture.Server.RuleHost.Time;

        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: checkpoint));
        Assert.Equal(before, fixture.Server.Arena.ComputeHash());
        Assert.Equal(time, fixture.Server.RuleHost.Time);
    }
    [Fact]
    public void StandaloneRewindSuppressesItsTargetGroupUntilTheNextWorldTick() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: Name(value: "score"), Kind: CellKind.Int, Min: 0, Max: 100, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0))]),
                new WorldStateRow(Name: Name(value: "undo"), Kind: CellKind.Int, Min: 0, Max: 1, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0))]),
            ]),
            Rules = [
                new WorldRule(Name: Name(value: "advance"), Effects: [new ActionEffect.AddState(State: "score", Value: 1)]),
                new WorldRule(Name: Name(value: "rewind"), Effects: [new ActionEffect.RewindTurn(Group: Name(value: "turn"))],
                    Gate: new ActionPredicate.CompareState(State: "undo", Comparison: ActionStateComparison.Equal, Value: 1), Mode: ActionTriggerMode.Edge),
            ],
            RuleGroupsRaw = [new RuleGroupDeclaration(Name: Name(value: "turn"), Shape: RuleGroupShape.Staged,
                Steps: [new RuleGroupStep(Name(value: "advance"))], Undo: new RuleGroupUndo([Name(value: "score")], 2))],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        Assert.Equal(1L, ReadSlot(fixture: fixture, row: "score"));
        Assert.True(condition: fixture.Server.Arena.Catalog.TryResolve(handle: out var undo, lane: StateLane.Document, name: "undo"));
        Assert.True(condition: fixture.Server.Arena.TryWrite(rowOrdinal: undo.Ordinal, key: fixture.Server.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey), operand: 1L, write: StateWriteKind.Set, reason: out var reason), userMessage: reason);

        fixture.Step();
        Assert.Equal(0L, ReadSlot(fixture: fixture, row: "score"));
        Assert.False(fixture.Server.Arena.UndoTurnPending("turn"));

        fixture.Step();
        Assert.Equal(1L, ReadSlot(fixture: fixture, row: "score"));
    }
}
