using Puck.Commands;
using System.Numerics;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class WorldScopedReplayTests {
    [Fact]
    public void AnEmptyKeyedInventoryCanGrowAndReplayRepeatedly() {
        using var fixture = Fixtures.FreshServer(GrowingInventory());
        Request(fixture, 4);
        Assert.Equal(0, fixture.Server.ScopedReplays[0].Checkpoint().Start!.Capacities[0]);
        for (var drive = 0; drive < 3; drive++) {
            Request(fixture, 1);
            for (var tick = 0; tick < 6; tick++) { fixture.Step(); }
            Assert.Equal(drive == 0 ? 3 : 4, Read(fixture, "control", "status"));
            Assert.Equal(7, Read(fixture, "counter", "card"));
            Assert.False(fixture.Server.ScopedReplays[0].Checkpoint().Active);
        }
        Request(fixture, 3);
        Assert.Equal(0, Read(fixture, "control", "status"));
        Assert.False(fixture.Server.ScopedReplays[0].Checkpoint().Active);
    }

    [Fact]
    public void ScopedPrivateRollsMatchAtEveryTick() {
        var document = Document();
        var draw = new Draw(PrivateStream: true, Timing: DrawTiming.Event, Generator: new StateGenerator(Source: GeneratorSource.StreamDraw));
        document = document.WithWorldState(document.AuthoredState.Select(row => row.Name.Value == "counter" ? row with { Draw = draw } : row).ToArray()) with {
            Rules = [new WorldRule(Name: CellName.Parse("roll"), Effects: [new ActionEffect.Generate("counter")],
                Gate: new ActionPredicate.CompareState(State: "control", Key: "status", Comparison: ExpressionOp.Greater, Value: 0),
                Mode: ActionTriggerMode.Level)]
        };
        using var fixture = Fixtures.FreshServer(document, drawKeys: WorldPrivateDrawKeys.Create());
        Request(fixture, 4);
        for (var run = 0; run < 2; run++) {
            Request(fixture, 1);
            for (var tick = 0; tick < 6; tick++) { fixture.Step(); }
            Assert.Equal(run == 0 ? 3 : 4, Read(fixture, "control", "status"));
            Assert.NotEmpty(fixture.Server.ScopedReplays[0].Checkpoint().Words);
        }
        var scope = fixture.Server.ScopedReplays[0];
        scope.RestoreCheckpoint(scope.Checkpoint() with { Words = [] });
        Request(fixture, 1);
        for (var tick = 0; tick < 6; tick++) { fixture.Step(); }
        Assert.Equal(-1, Read(fixture, "control", "status"));
    }

    [Fact]
    public void AWholeTapeAndHistoryReproduceAnExtraPrivateReadThatRewound() {
        var document = Document();
        var draw = new Draw(PrivateStream: true, Timing: DrawTiming.Event, Generator: new StateGenerator(Source: GeneratorSource.StreamDraw));
        document = document.WithWorldState([
            .. document.AuthoredState.Select(row => row.Name.Value == "counter" ? row with { Draw = draw } : row),
            StateFixtures.IntSlot("zero")
        ]) with {
            Rules = [new WorldRule(Name: CellName.Parse("extra-roll"), Effects: [new ActionEffect.Transaction([
                new ActionEffect.Generate("counter"),
                new ActionEffect.SetState("counter", Expression: new ExpressionProgram([
                    Instruction.Constant(1m), Instruction.Operand(name: "zero"), Instruction.Of(ExpressionOp.Divide)
                ]))
            ])], Gate: new ActionPredicate.CompareState("witness", ExpressionOp.Greater, 0), Mode: ActionTriggerMode.Level)]
        };
        using var harness = new WorldHistoryHarness(definition: document, on: false);
        void RequestScope(int request, int ticks) {
            harness.Tape.RequestScope(harness.Tape.Scopes[0], request, Principal.Console);
            harness.Steps(ticks);
        }
        RequestScope(4, 2);
        RequestScope(1, 8);
        var scope = harness.Fixture.Server.ScopedReplays[0];
        var first = scope.Checkpoint();
        Assert.Empty(first.Words);
        Assert.Equal(3, Read(harness.Fixture, "control", "status"));

        Assert.True(harness.History.TryOn(WorldHistory.DefaultWindowSeconds, WorldHistory.DefaultBudgetBytes, out var reason), reason);
        Assert.True(harness.Tape.TryBeginRecording("extra-private-read", out reason), reason);
        harness.Submit(new WorldMutation.UpsertStateCell(Principal.Console, "witness", "$value", 1, WorldDocumentWriteKind.Set));
        RequestScope(1, 8);
        Assert.Equal(first.Hashes[^1], scope.Hash());
        Assert.Equal(-1, Read(harness.Fixture, "control", "status"));
        var target = harness.Tick;
        harness.Steps(2);
        var stop = harness.Tape.StopRecording();
        Assert.True(stop.Verdict!.Passing, stop.Verdict.Describe());
        Assert.True(harness.Tape.Verify("extra-private-read").Passing);
        _ = harness.SeekAndProve(target);
    }

    [Fact]
    public void ARefusedScopeRestoreEndsItsDriveAndCanBeStartedAgain() {
        using var fixture = Fixtures.FreshServer(GrowingInventory());
        Request(fixture, 4);
        Request(fixture, 1);
        Assert.True(fixture.Server.ScopedReplays[0].Checkpoint().Recording);
        fixture.Server.Arena.GrowthAdmission = layout => layout[0].CellCapacity == 0 ? "scope reservation refused for this witness" : null;
        Request(fixture, 3);
        var scope = fixture.Server.ScopedReplays[0];
        var refused = scope.Checkpoint();
        Assert.Equal(-1, Read(fixture, "control", "status"));
        Assert.False(refused.Active);
        Assert.False(refused.Recording);
        Assert.Equal(0, refused.Cursor);
        Assert.Empty(refused.Hashes);
        fixture.Server.Arena.GrowthAdmission = null;
        scope.RestoreCheckpoint(refused);
        Request(fixture, 1);
        for (var tick = 0; tick < 6; tick++) { fixture.Step(); }
        Assert.Equal(3, Read(fixture, "control", "status"));
    }

    private static WorldDefinition GrowingInventory() {
        var document = Document();
        return document with {
            StateRaw = document.StateRaw! with { World = document.State.Select(row => row.Name.Value == "counter"
                ? new WorldStateRow(Name: row.Name, Kind: CellKind.Int, Domain: StateDomain.Keys.Instance) : row).ToArray() },
            ReplaysRaw = [document.ReplaysRaw![0] with {
                Inputs = [new WorldScopedReplayInput(1, Principal.Seat(0), [new WorldScopedReplayCell(Key: "card", Row: "counter", Value: 7)])]
            }]
        };
    }

    [Fact]
    public void AWholeSessionTapeRecordsScopeControlsAndDerivesProgramInputsExactlyOnce() {
        using var harness = new WorldHistoryHarness(definition: Document(), on: false, seats: 1);

        harness.Steps(count: 2);
        Assert.True(condition: harness.Tape.TryBeginRecording(name: "scoped-controls", refusal: out var refusal), userMessage: refusal);
        harness.Tape.RequestScope(scope: harness.Tape.Scopes[0], request: 4, principal: Principal.Console);
        harness.Steps(count: 2);
        harness.Tape.RequestScope(scope: harness.Tape.Scopes[0], request: 1, principal: Principal.Console);
        harness.Steps(count: 8);
        harness.Tape.RequestScope(scope: harness.Tape.Scopes[0], request: 1, principal: Principal.Console);
        harness.Steps(count: 8);
        Assert.Equal(4, Read(fixture: harness.Fixture, key: "status", row: "control"));
        var stop = harness.Tape.StopRecording();

        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());
        Assert.True(condition: harness.Tape.Verify(name: "scoped-controls").Passing);
    }
    [Fact]
    public void BodyInputsAreRecordedAndReenterTheOrdinaryDriveDoors() {
        var definition = Document();

        definition = definition with { ReplaysRaw = [definition.ReplaysRaw![0] with { Bodies = [0] }] };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat();
        Request(fixture: fixture, request: 4);
        Request(fixture: fixture, request: 1);
        fixture.Server.ApplyCommand(new WorldCommand.SnapPose(Principal.Seat(slot: 0), 0, new Vector3(x: 2, y: 10, z: 3), 0, 0, 0, SnapPoseMode.Pose));
        var channels = new ChannelValues();

        channels[0] = Puck.Maths.FixedQ4816.One;
        fixture.Server.EnqueueIntent(submission: Fixtures.Input(fixture.Server, Principal.Seat(slot: 0), 0,
            new PlayerIntent(Channels: channels, SourceRay: null), fixture.Server.NextInputTick));
        for (var i = 0; (i < 6); i++) { fixture.Step(); }
        var tape = fixture.Server.ScopedReplays[0].Checkpoint();

        Assert.Single(collection: tape.Commands);
        Assert.Single(collection: tape.Intents);
        Assert.Equal(Puck.Maths.FixedQ4816.One, tape.Intents[0].Intent.Intent.Channels[0]);
        var scope = fixture.Server.ScopedReplays[0];

        Assert.Throws<InvalidOperationException>(testCode: () => scope.RestoreCheckpoint(checkpoint: tape with { Bodies = [] }));
        Assert.Throws<InvalidOperationException>(testCode: () => scope.RestoreCheckpoint(checkpoint: tape with { Bodies = [tape.Bodies[0], tape.Bodies[0]] }));
        Assert.Throws<InvalidOperationException>(testCode: () => scope.RestoreCheckpoint(checkpoint: tape with { Commands = [(6, tape.Commands[0].Command)] }));
        Assert.Throws<InvalidOperationException>(testCode: () => scope.RestoreCheckpoint(checkpoint: tape with { Commands = [(1, tape.Commands[0].Command), (0, tape.Commands[0].Command)] }));
        Assert.Throws<InvalidOperationException>(testCode: () => scope.RestoreCheckpoint(checkpoint: tape with { Intents = [(-1, tape.Intents[0].Intent)] }));
        Assert.Throws<InvalidOperationException>(testCode: () => scope.RestoreCheckpoint(checkpoint: tape with { Intents = [(1, tape.Intents[0].Intent), (0, tape.Intents[0].Intent)] }));
        Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 6); i++) { fixture.Step(); }
        Assert.Equal(4, Read(fixture: fixture, key: "status", row: "control"));
    }

    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [
            StateFixtures.IntSlot("counter"), StateFixtures.IntSlot("witness"),
            new WorldStateRow(Name: CellName.Parse(candidate: "control"), Kind: CellKind.Int,
                Cells: [StateFixtures.Cell("request", 0), StateFixtures.Cell("status", 0),
                    StateFixtures.Cell("cursor", 0), StateFixtures.Cell("completed", 0)])
        ]),
        GrantsRaw = [new WorldGrant(Capability: WorldCapability.Edit, Grantee: Principal.Seat(slot: 0),
            Subject: GrantSubject.State(name: "counter"), Exclusive: false)],
        ReplaysRaw = [new WorldScopedReplay("scope", ["counter"], [], [Principal.Seat(slot: 0)], "control", 6,
            [new WorldScopedReplayInput(1, Principal.Seat(slot: 0), [new WorldScopedReplayCell(Key: "$value", Row: "counter", Value: 7)])])],
    };
    private static void Request(WorldFixture fixture, int request) {
        fixture.Server.EnqueueMutation(fixture.Server.ScopedReplays[0].CreateRequest(request: request, principal: Principal.Console));
        fixture.Step(); fixture.Step();
    }
    private static long Read(WorldFixture fixture, string row, string key = "$value") {
        Assert.True(condition: fixture.Server.Arena.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: row));
        Assert.True(condition: fixture.Server.Arena.Keys.TryResolve(CellName.Parse(candidate: key), out var cell));
        Assert.True(condition: fixture.Server.Arena.TryRead(handle.Ordinal, cell, out var value));
        return value.Raw;
    }

    [Fact]
    public void ScopeRestoresOnlyItsRowsAndDetectsChangedPlayback() {
        using var fixture = Fixtures.FreshServer(Document());

        Request(fixture: fixture, request: 4);
        Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 6); i++) { fixture.Step(); }
        Assert.Equal(3, Read(fixture: fixture, key: "status", row: "control"));
        Assert.Equal(7, Read(fixture, "counter"));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "witness", "$value", 731, WorldDocumentWriteKind.Set));
        Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 6); i++) { fixture.Step(); }
        Assert.Equal(4, Read(fixture: fixture, key: "status", row: "control"));
        Assert.Equal(731, Read(fixture, "witness"));

        Request(fixture: fixture, request: 1);
        fixture.Step();
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "counter", "$value", 99, WorldDocumentWriteKind.Set));
        fixture.Step();
        for (var i = 0; (i < 6); i++) { fixture.Step(); }
        Assert.Equal(-1, Read(fixture: fixture, key: "status", row: "control"));
    }
    [Fact]
    public void WholeAuthorityCheckpointRetainsAnActiveScopedTapeAndItsOriginalHashes() {
        using var fixture = Fixtures.FreshServer(Document());

        Request(fixture: fixture, request: 4);
        Request(fixture: fixture, request: 1);
        fixture.Step();
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes, out var decoded, out reason), userMessage: reason);
        var before = WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0);

        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        Assert.Equal(before, WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0));
        for (var i = 0; (i < 6); i++) { fixture.Step(); }
        Assert.Equal(3, Read(fixture: fixture, key: "status", row: "control"));
        Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 6); i++) { fixture.Step(); }
        Assert.Equal(4, Read(fixture: fixture, key: "status", row: "control"));
    }
}
