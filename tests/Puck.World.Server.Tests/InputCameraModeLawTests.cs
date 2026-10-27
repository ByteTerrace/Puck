using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class InputCameraModeLawTests {
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CameraStateAndApplicationShareOneValidatedBarrier(int refusal) {
        var basis = Fixtures.BuildCameraBodyDocument();
        IReadOnlyDictionary<string, string>[] combinations = [
            new Dictionary<string, string> { ["camera"] = "seat", ["route"] = "none" },
            new Dictionary<string, string> { ["camera"] = "free", ["route"] = refusal == 1 ? "none" : "body" },
        ];
        var definition = basis with {
            SeatModesRaw = [new(Name: "camera", DefaultState: "seat", States: [new("seat"), new("free", Target: WorldSeatModeState.CameraTarget)])],
            ViewsRaw = basis.Views with { CameraRig = new("test-camera", WorldCameraProgram.CurrentVersion,
                [new WorldCameraProgramOp.LookAt(Subject: null, FocusDistance: 1f), new WorldCameraProgramOp.FieldOfView(new BindableScalar(0.9f))]) },
            InputModes = new(basis.Channels.Select(row => row.Name).ToArray(), [
                new("camera", new(WorldInputModeSelectorKind.SeatState, "camera"), "seat", [
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
        var principal = Principal.Seat(0);
        var owner = fixture.Server.Input.Admit(principal, 0, []);
        var epoch = owner.Epoch;
        var camera = Enumerable.Range(0, fixture.Server.Population.Capacity).Single(index =>
            fixture.Server.Population.InhabitantPlacementId(index) == WorldSeatModeState.CameraPlacementIdPrefix + "0");
        var actor = refusal == 2 ? Principal.Addon("camera-mode-test") : Principal.Console;
        if (refusal == 2) { fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(0), false), Principal.Console); }
        var admitted = fixture.Server.Input.TrySetSeatState(principal, "camera", "free", 19, out var reason, actor);
        Assert.Equal(refusal == 0, admitted);
        var publication = fixture.Server.Input.Publish().Span[0];
        if (refusal != 0) {
            Assert.NotEmpty(reason);
            Assert.Equal(epoch, owner.Epoch);
            Assert.Contains(publication.SeatStates.ToArray(), cell => cell.Family == "camera" && cell.State == "seat");
            Assert.DoesNotContain(fixture.Server.GrantTable.Applications(principal), row => row.Target == GrantSubject.Body(camera));
            return;
        }
        Assert.Equal(epoch + 1, owner.Epoch);
        Assert.Equal(new ulong[] { 19 }, owner.LastSwitch.ConsumedActivations.ToArray());
        Assert.Contains(publication.SeatStates.ToArray(), cell => cell.Family == "camera" && cell.State == "free");
        Assert.Contains(fixture.Server.GrantTable.Applications(principal), row => row.Target == GrantSubject.Body(camera));
        Assert.True(fixture.Server.GrantTable.Allows(principal, WorldCapability.Drive, GrantSubject.Body(camera)).IsAllowed);
        Assert.True(fixture.Server.Input.TrySetSeatState(principal, "camera", "seat", 20, out reason, actor), reason);
        Assert.Equal(epoch + 2, owner.Epoch);
        Assert.Equal(new ulong[] { 20 }, owner.LastSwitch.ConsumedActivations.ToArray());
        Assert.DoesNotContain(fixture.Server.GrantTable.Applications(principal), row => row.Target == GrantSubject.Body(camera));
    }
}
