using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class InputCameraModeLawTests {
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void CameraStateAndApplicationShareOneValidatedBarrier(int refusal) {
        var basis = Fixtures.BuildCameraBodyDocument();
        IReadOnlyDictionary<string, string>[] combinations = [
            new Dictionary<string, string> { ["camera"] = "seat", ["route"] = "none" },
            new Dictionary<string, string> { ["camera"] = "free", ["route"] = ((refusal == 1) ? "none" : "body") },
        ];
        var definition = basis with {
            SeatModesRaw = [new(Name: "camera", DefaultState: "seat", States: [new("seat"), new("free", Target: WorldSeatModeState.CameraTarget)])],
            ViewsRaw = basis.Views with {
                CameraRig = new("test-camera", WorldCameraProgram.CurrentVersion,
                [new WorldCameraProgramOp.LookAt(Subject: null, FocusDistance: 1f), new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0.9f))]),
            },
            InputModes = new(basis.Channels.Select(selector: row => row.Name).ToArray(), [
                new("camera", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "camera"), "seat", [
                    new("seat", [], ["seat"]), new("free", [], ["free"]),
                ]),
                new("route", new(WorldInputModeSelectorKind.Engagement), "none", [
                    new("none", [], ["none", "screen", "machine", "tabletop"]), new("body", [], ["body"]),
                ]),
            ], combinations),
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat();
        fixture.Step();
        var principal = Principal.Seat(slot: 0);
        var owner = fixture.Server.Input.Admit(bodyIndex: 0, principal: principal, sources: []);
        var epoch = owner.Epoch;
        var camera = Enumerable.Range(0, fixture.Server.Population.Capacity).Single(predicate: index =>
            (fixture.Server.Population.InhabitantPlacementId(index: index) == (WorldSeatModeState.CameraPlacementIdPrefix + "0")));
        var actor = ((refusal == 2) ? Principal.Addon(name: "camera-mode-test") : Principal.Console);

        if (refusal == 2) { fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(index: 0), false), Principal.Console); }
        var admitted = fixture.Server.Input.TrySetSeatState(activation: 19, actor: actor, family: "camera", principal: principal, reason: out var reason, state: "free");

        Assert.Equal(actual: admitted, expected: (refusal == 0));
        var publication = fixture.Server.Input.Publish().Span[0];

        if (refusal != 0) {
            Assert.NotEmpty(collection: reason);
            Assert.Equal(epoch, owner.Epoch);
            Assert.Contains(collection: publication.SeatStates.ToArray(), filter: cell => ((cell.Family == "camera") && (cell.State == "seat")));
            Assert.DoesNotContain(collection: fixture.Server.GrantTable.Applications(principal: principal), filter: row => (row.Target == GrantSubject.Body(index: camera)));
            return;
        }
        Assert.Equal((epoch + 1), owner.Epoch);
        Assert.Equal(new ulong[] { 19 }, owner.LastSwitch.ConsumedActivations.ToArray());
        Assert.Contains(collection: publication.SeatStates.ToArray(), filter: cell => ((cell.Family == "camera") && (cell.State == "free")));
        Assert.Contains(collection: fixture.Server.GrantTable.Applications(principal: principal), filter: row => (row.Target == GrantSubject.Body(index: camera)));
        Assert.True(condition: fixture.Server.GrantTable.Allows(principal, WorldCapability.Drive, GrantSubject.Body(index: camera)).IsAllowed);
        Assert.True(condition: fixture.Server.Input.TrySetSeatState(activation: 20, actor: actor, family: "camera", principal: principal, reason: out reason, state: "seat"), userMessage: reason);
        Assert.Equal((epoch + 2), owner.Epoch);
        Assert.Equal(new ulong[] { 20 }, owner.LastSwitch.ConsumedActivations.ToArray());
        Assert.DoesNotContain(collection: fixture.Server.GrantTable.Applications(principal: principal), filter: row => (row.Target == GrantSubject.Body(index: camera)));
    }
}
