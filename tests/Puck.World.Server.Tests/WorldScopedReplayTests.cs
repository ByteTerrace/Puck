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
