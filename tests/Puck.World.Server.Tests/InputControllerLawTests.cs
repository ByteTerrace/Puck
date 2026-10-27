using Puck.Commands;
using Puck.Maths;
using Puck.Networking;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class InputControllerLawTests {
    [Fact]
    public void ScriptedDurationsRetainTheirActorAndAdmittedHistoryAcrossCheckpointRestore() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with { ChannelsRaw = [.. basis.Channels, new("tap", ChannelShape.Binary, Composition: true)] };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.JoinSeat();
        fixture.Step();
        var actor = Principal.Addon("script-test");
        fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(0), false, Budget: 8,
            Reach: new(["tap"])), Principal.Console);
        var body = fixture.Server.Body(0)!;
        Assert.True(body.Channels.TryGetOrdinal("tap", out var address));
        Assert.True(fixture.Server.Population.InputCatalog.TryGetLogicalId("tap", out var logical));
        body.PressChannel(address, FixedQ4816.One, 0.1f, FixedQ4816.One, actor);
        fixture.Step();
        var participant = Assert.Single(fixture.Server.Input.Capture().Owners);
        Assert.Equal(actor, participant.Controller!.Value.Actor);
        Assert.True(fixture.Server.Input.TryGet(participant.Principal, out var owner));
        Assert.True(owner!.TryHeld(logical, out var hold));
        Assert.True(owner.TryEvent(logical, WorldInputEventKind.Press, 0, out _));
        Assert.Equal(FixedQ4816.One, body.ChannelReadComposed[address]);
        // Queue another segment before the checkpoint; its author must not become World or Console on restore.
        body.EnqueueRun(default(PlayerIntent).WithChannel(address, FixedQ4816.One), 0.2f, actor);
        Assert.True(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), reason);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint!), out var decoded, out reason), reason);
        using var restored = Fixtures.FreshServer(definition);
        restored.Server.RestoreCheckpoint(decoded!);
        for (var index = 0; index < 12; index++) {
            fixture.Step(); restored.Step();
            var tick = fixture.Server.Input.Interval.Tick;
            Assert.Equal(WorldStateHashComposition.HashAuthoritative(fixture.Server, tick),
                WorldStateHashComposition.HashAuthoritative(restored.Server, tick));
        }
        Assert.False(owner.TryHeld(logical, out _));
        Assert.True(owner.TryEvent(logical, WorldInputEventKind.Release, 0, out var released));
        Assert.Equal(hold.Press, released.HoldPress);
        body.PressChannel(address, FixedQ4816.One, 1f, FixedQ4816.One, actor);
        fixture.Step();
        Assert.True(owner.TryHeld(logical, out _));
        var replacement = Principal.Addon("replacement-script");
        fixture.Server.Grant(new(replacement, WorldCapability.Drive, GrantSubject.Body(0), false, Budget: 8,
            Reach: new(["tap"])), Principal.Console);
        body.PressChannel(address, FixedQ4816.One, 0.1f, FixedQ4816.One, replacement);
        fixture.Step();
        Assert.False(owner.TryHeld(logical, out _));
        Assert.True(owner.TryEvent(logical, WorldInputEventKind.Release, 0, out _));
        var replacementOwner = Assert.Single(fixture.Server.Input.Capture().Owners, row => row.Controller?.Actor == replacement);
        Assert.True(fixture.Server.Input.TryGet(replacementOwner.Principal, out var replacing));
        Assert.True(replacing!.TryHeld(logical, out _));
        fixture.Server.Revoke(new(replacement, WorldCapability.Drive, GrantSubject.Body(0), false), Principal.Console);
        fixture.Step();
        Assert.Equal(FixedQ4816.Zero, body.ChannelReadComposed[address]);
        Assert.False(replacing.TryHeld(logical, out _));
        Assert.True(replacing.TryEvent(logical, WorldInputEventKind.Cancel, 0, out _));
    }

    [Fact]
    public void AControllerFollowsItsTargetsAdmittedModeAndCannotReviveCancelledInput() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("first", ChannelShape.Binary, Composition: true), new("second", ChannelShape.Binary, Composition: true)],
            SeatModesRaw = [new(Name: "focus", DefaultState: "first", States: [new("first"), new("second")])],
            InputModes = new(basis.Channels.Select(row => row.Name).ToArray(), [
                new("focus", new(WorldInputModeSelectorKind.SeatState, "focus"), "first", [
                    new("first", ["first"], ["first"]), new("second", ["second"], ["second"]),
                ]),
            ]),
        };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.JoinSeat();
        fixture.Step();
        var primary = Principal.Seat(0);
        _ = fixture.Server.Input.Admit(primary, 0, []);
        var actor = Principal.Addon("modal-snapshot");
        fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(0), false, Budget: 8,
            Reach: new(["first", "second"])), Principal.Console);
        var body = fixture.Server.Body(0)!;
        var tick = fixture.Server.Input.Interval.Tick;
        Assert.True(body.Channels.TryGetOrdinal("first", out var firstAddress));
        var first = default(PlayerIntent).WithChannel(firstAddress, FixedQ4816.One);
        Assert.True(fixture.Server.Input.TryPublishSnapshot(actor, 0, "guest", first, tick, out var session, out var batch, out var reason), reason);
        Assert.True(fixture.Server.Input.TryAdmit(session, batch, out reason), reason);
        Assert.True(fixture.Server.Input.TryGet(session, out var controller));
        Assert.True(fixture.Server.Population.InputCatalog.TryGetLogicalId("first", out var firstLogical));
        Assert.True(fixture.Server.Population.InputCatalog.TryGetLogicalId("second", out var secondLogical));
        var epoch = controller!.Epoch;
        Assert.True(fixture.Server.Input.TrySetSeatState(primary, "focus", "second", 0, out reason), reason);
        Assert.Equal(epoch + 1, controller.Epoch);
        Assert.True(controller.TryEvent(firstLogical, WorldInputEventKind.Cancel, 0, out _));
        Assert.False(fixture.Server.Input.TryAdmit(session, batch, out _));
        Assert.True(body.Channels.TryGetOrdinal("second", out var secondAddress));
        Assert.Equal(firstAddress, secondAddress);
        var second = default(PlayerIntent).WithChannel(secondAddress, FixedQ4816.One);
        Assert.True(fixture.Server.Input.TryPublishSnapshot(actor, 0, "guest", second, tick, out var sameSession, out batch, out reason), reason);
        Assert.Equal(session, sameSession);
        Assert.True(fixture.Server.Input.TryAdmit(session, batch, out reason), reason);
        Assert.False(controller.TryHeld(secondLogical, out _));
        Assert.Empty(batch.Events.ToArray());
        fixture.Step();
        tick = fixture.Server.Input.Interval.Tick;
        Assert.True(fixture.Server.Input.TryPublishSnapshot(actor, 0, "guest", second, tick, out sameSession, out batch, out reason), reason);
        Assert.True(fixture.Server.Input.TryAdmit(session, batch, out reason), reason);
        Assert.True(controller.TryHeld(secondLogical, out _));
        Assert.False(controller.TryHeld(firstLogical, out _));
    }

    [Fact]
    public void OneActorHasIndependentTargetSessionsWithoutMintedGrantsAndTheyRestoreExactly() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with { ChannelsRaw = [.. basis.Channels, new("tap", ChannelShape.Binary, Composition: true)] };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.JoinSeat(0);
        fixture.JoinSeat(1);
        fixture.Step();
        var actor = Principal.Addon("snapshot-test");
        for (var body = 0; body < 2; body++) { fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(body), false, Budget: 8, Reach: new(["tap"])), Principal.Console); }
        var runtime = fixture.Server.Input;
        var tick = runtime.Interval.Tick;
        Assert.True(fixture.Server.Body(0)!.Channels.TryGetOrdinal("tap", out var address));
        Assert.True(fixture.Server.Population.InputCatalog.TryGetLogicalId("tap", out var channel));
        var held = default(PlayerIntent).WithChannel(address, FixedQ4816.One);
        Assert.True(runtime.TryPublishSnapshot(actor, 0, "guest", held, tick, out var first, out var press, out var reason), reason);
        var submission = new IntentSubmission(tick, 0, press.Image, actor, Input: press, InputOwner: first);
        var writer = new WireWriter();
        Assert.True(WorldWireCodec.TryWriteIntentSubmission(writer, submission));
        var reader = new WireReader(writer.ToArray());
        var decodedSubmission = WorldWireCodec.ReadIntentSubmission(ref reader);
        Assert.True(reader.TryFinish(out _));
        Assert.Equal(first, decodedSubmission.InputOwner);
        Assert.Equal(actor, decodedSubmission.Principal);
        var bodyZero = fixture.Server.Body(0)!;
        Assert.False(fixture.Server.ApplyIntentSubmission(bodyZero, decodedSubmission with { Principal = Principal.Console }).IsAccepted);
        Assert.True(fixture.Server.ApplyIntentSubmission(bodyZero, decodedSubmission).IsAccepted);
        Assert.True(runtime.TryPublishSnapshot(actor, 1, "guest", held, tick, out var second, out var other, out reason), reason);
        Assert.NotEqual(first, second);
        Assert.Equal(PrincipalKind.Session, first.Kind);
        Assert.True(runtime.TryAdmit(second, other, out reason, 1), reason);
        Assert.True(runtime.IsController(first, actor, 0));
        Assert.False(runtime.IsController(first, actor, 1));
        Assert.False(runtime.IsController(first, Principal.Console, 0));
        Assert.False(fixture.Server.GrantTable.Allows(first, WorldCapability.Drive, GrantSubject.Body(0)).IsAllowed);
        Assert.True(runtime.TryPublishSnapshot(actor, 0, "guest", default, tick, out var releasing, out var release, out reason), reason);
        Assert.Equal(first, releasing);
        Assert.True(runtime.TryAdmit(first, release, out reason, 0), reason);
        Assert.True(runtime.TryGet(first, out var owner));
        Assert.True(owner!.TryEvent(channel, WorldInputEventKind.Press, 0, out var pressed));
        Assert.True(owner.TryEvent(channel, WorldInputEventKind.Release, 0, out var released));
        Assert.Equal(pressed.Id, released.HoldPress);
        Assert.True(runtime.TryGet(second, out var otherOwner));
        Assert.True(otherOwner!.TryHeld(channel, out _));
        Assert.Equal(FixedQ4816.One, fixture.Server.GrantTable.ReadSessionChannel(second.Index, channel));
        Assert.True(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out reason), reason);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint!), out var decoded, out reason), reason);
        using var restored = Fixtures.FreshServer(definition);
        restored.Server.RestoreCheckpoint(decoded!);
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(fixture.Server, tick), WorldStateHashComposition.HashAuthoritative(restored.Server, tick));
        Assert.True(restored.Server.Input.TryPublishSnapshot(actor, 1, "guest", held, tick, out var resumed, out var unchanged, out reason), reason);
        Assert.Equal(second, resumed);
        Assert.Empty(unchanged.Events.ToArray());
        Assert.True(restored.Server.Input.TryAdmit(resumed, unchanged, out reason, 1), reason);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++) {
            if (!restored.Server.Input.TryPublishSnapshot(actor, 1, "guest", held, tick, out resumed, out unchanged, out reason) ||
                !restored.Server.Input.TryAdmit(resumed, unchanged, out reason, 1)) { throw new InvalidOperationException(reason); }
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        restored.Step();
        tick = restored.Server.Input.Interval.Tick;
        restored.Server.Input.CompleteSnapshots(actor, "guest", tick);
        Assert.True(restored.Server.Input.TryGet(second, out otherOwner));
        Assert.False(otherOwner!.TryHeld(channel, out _));
        Assert.Equal(FixedQ4816.Zero, restored.Server.GrantTable.ReadSessionChannel(second.Index, channel));
        Assert.True(otherOwner.TryEvent(channel, WorldInputEventKind.Release, 0, out _));
        Assert.True(restored.Server.Input.TryPublishSnapshot(actor, 1, "guest", held, tick, out resumed, out unchanged, out reason), reason);
        Assert.True(restored.Server.Input.TryAdmit(resumed, unchanged, out reason, 1), reason);
        restored.Server.Revoke(new(actor, WorldCapability.Drive, GrantSubject.Body(1), false), Principal.Console);
        restored.Server.Input.RefreshPermissions();
        Assert.True(restored.Server.Input.TryGet(second, out otherOwner));
        Assert.False(otherOwner!.TryHeld(channel, out _));
        Assert.True(otherOwner.TryEvent(channel, WorldInputEventKind.Cancel, 0, out _));
    }
}
