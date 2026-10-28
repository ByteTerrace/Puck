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
        var actor = Principal.Addon(name: "script-test");

        fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(index: 0), false, Budget: 8,
            Reach: new(["tap"])), Principal.Console);
        var body = fixture.Server.Body(index: 0)!;

        Assert.True(condition: body.Channels.TryGetOrdinal(name: "tap", ordinal: out var address));
        Assert.True(condition: fixture.Server.Population.InputCatalog.TryGetLogicalId(id: out var logical, name: "tap"));
        body.PressChannel(address, FixedQ4816.One, 0.1f, FixedQ4816.One, actor);
        fixture.Step();
        var participant = Assert.Single(collection: fixture.Server.Input.Capture().Owners);

        Assert.Equal(actor, participant.Controller!.Value.Actor);
        Assert.True(condition: fixture.Server.Input.TryGet(participant.Principal, out var owner));
        Assert.True(condition: owner!.TryHeld(channel: logical, hold: out var hold));
        Assert.True(condition: owner.TryEvent(channel: logical, kind: WorldInputEventKind.Press, occurrence: 0, record: out _));
        Assert.Equal(FixedQ4816.One, body.ChannelReadComposed[address]);
        // Queue another segment before the checkpoint; its author must not become World or Console on restore.
        body.EnqueueRun(default(PlayerIntent).WithChannel(ordinal: address, value: FixedQ4816.One), 0.2f, actor);
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!), out var decoded, out reason), userMessage: reason);
        using var restored = Fixtures.FreshServer(definition);

        restored.Server.RestoreCheckpoint(checkpoint: decoded!);
        for (var index = 0; (index < 12); index++) {
            fixture.Step(); restored.Step();
            var tick = fixture.Server.Input.Interval.Tick;

            Assert.Equal(WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: tick),
                WorldStateHashComposition.HashAuthoritative(server: restored.Server, tick: tick));
        }
        Assert.False(condition: owner.TryHeld(channel: logical, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: logical, kind: WorldInputEventKind.Release, occurrence: 0, record: out var released));
        Assert.Equal(hold.Press, released.HoldPress);
        body.PressChannel(address, FixedQ4816.One, 1f, FixedQ4816.One, actor);
        fixture.Step();
        Assert.True(condition: owner.TryHeld(channel: logical, hold: out _));
        var replacement = Principal.Addon(name: "replacement-script");

        fixture.Server.Grant(new(replacement, WorldCapability.Drive, GrantSubject.Body(index: 0), false, Budget: 8,
            Reach: new(["tap"])), Principal.Console);
        body.PressChannel(address, FixedQ4816.One, 0.1f, FixedQ4816.One, replacement);
        fixture.Step();
        Assert.False(condition: owner.TryHeld(channel: logical, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: logical, kind: WorldInputEventKind.Release, occurrence: 0, record: out _));
        var replacementOwner = Assert.Single(collection: fixture.Server.Input.Capture().Owners, predicate: row => (row.Controller?.Actor == replacement));

        Assert.True(condition: fixture.Server.Input.TryGet(replacementOwner.Principal, out var replacing));
        Assert.True(condition: replacing!.TryHeld(channel: logical, hold: out _));
        fixture.Server.Revoke(new(replacement, WorldCapability.Drive, GrantSubject.Body(index: 0), false), Principal.Console);
        fixture.Step();
        Assert.Equal(FixedQ4816.Zero, body.ChannelReadComposed[address]);
        Assert.False(condition: replacing.TryHeld(channel: logical, hold: out _));
        Assert.True(condition: replacing.TryEvent(channel: logical, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out _));
    }
    [Fact]
    public void AControllerFollowsItsTargetsAdmittedModeAndCannotReviveCancelledInput() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("first", ChannelShape.Binary, Composition: true), new("second", ChannelShape.Binary, Composition: true)],
            SeatModesRaw = [new(Name: "focus", DefaultState: "first", States: [new("first"), new("second")])],
            InputModes = new(basis.Channels.Select(selector: row => row.Name).ToArray(), [
                new("focus", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "focus"), "first", [
                    new("first", ["first"], ["first"]), new("second", ["second"], ["second"]),
                ]),
            ]),
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat();
        fixture.Step();
        var primary = Principal.Seat(slot: 0);

        _ = fixture.Server.Input.Admit(bodyIndex: 0, principal: primary, sources: []);
        var actor = Principal.Addon(name: "modal-snapshot");

        fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(index: 0), false, Budget: 8,
            Reach: new(["first", "second"])), Principal.Console);
        var body = fixture.Server.Body(index: 0)!;
        var tick = fixture.Server.Input.Interval.Tick;

        Assert.True(condition: body.Channels.TryGetOrdinal(name: "first", ordinal: out var firstAddress));
        var first = default(PlayerIntent).WithChannel(ordinal: firstAddress, value: FixedQ4816.One);

        Assert.True(condition: fixture.Server.Input.TryPublishSnapshot(actor, 0, "guest", first, tick, out var session, out var batch, out var reason), userMessage: reason);
        Assert.True(condition: fixture.Server.Input.TryAdmit(session, batch, out reason), userMessage: reason);
        Assert.True(condition: fixture.Server.Input.TryGet(input: out var controller, principal: session));
        Assert.True(condition: fixture.Server.Population.InputCatalog.TryGetLogicalId(id: out var firstLogical, name: "first"));
        Assert.True(condition: fixture.Server.Population.InputCatalog.TryGetLogicalId(id: out var secondLogical, name: "second"));
        Assert.True(condition: WorldSubmissionCodec.TryEncodeCommand(new WorldCommand.PressChannel(actor, 0, "first", FixedQ4816.One, null), out var pendingPress, out var codecFailure), userMessage: codecFailure.ToString());
        var epoch = controller!.Epoch;

        Assert.True(condition: fixture.Server.Input.TrySetSeatState(primary, "focus", "second", 0, out reason), userMessage: reason);
        Assert.Equal((epoch + 1), controller.Epoch);
        Assert.True(condition: controller.TryEvent(channel: firstLogical, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out _));
        Assert.False(condition: fixture.Server.Input.TryAdmit(session, batch, out _));
        Assert.True(condition: body.Channels.TryGetOrdinal(name: "second", ordinal: out var secondAddress));
        Assert.Equal(actual: secondAddress, expected: firstAddress);
        Assert.True(condition: WorldSubmissionCodec.TryDecodeCommand(bytes: pendingPress, command: out var decodedPress, failure: out codecFailure), userMessage: codecFailure.ToString());
        fixture.Server.ApplyCommand(decodedPress!);
        Assert.Contains("'first' is not active", fixture.Server.Population.PressRefusal(bodyIndex: 0));
        var second = default(PlayerIntent).WithChannel(ordinal: secondAddress, value: FixedQ4816.One);

        Assert.True(condition: fixture.Server.Input.TryPublishSnapshot(actor, 0, "guest", second, tick, out var sameSession, out batch, out reason), userMessage: reason);
        Assert.Equal(actual: sameSession, expected: session);
        Assert.True(condition: fixture.Server.Input.TryAdmit(session, batch, out reason), userMessage: reason);
        Assert.False(condition: controller.TryHeld(channel: secondLogical, hold: out _));
        Assert.Empty(collection: batch.Events.ToArray());
        fixture.Step();
        tick = fixture.Server.Input.Interval.Tick;
        Assert.True(condition: fixture.Server.Input.TryPublishSnapshot(actor, 0, "guest", second, tick, out sameSession, out batch, out reason), userMessage: reason);
        Assert.True(condition: fixture.Server.Input.TryAdmit(session, batch, out reason), userMessage: reason);
        Assert.True(condition: controller.TryHeld(channel: secondLogical, hold: out _));
        Assert.False(condition: controller.TryHeld(channel: firstLogical, hold: out _));
    }
    [Fact]
    public void OneActorHasIndependentTargetSessionsWithoutMintedGrantsAndTheyRestoreExactly() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with { ChannelsRaw = [.. basis.Channels, new("tap", ChannelShape.Binary, Composition: true)] };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat(slot: 0);
        fixture.JoinSeat(slot: 1);
        fixture.Step();
        var actor = Principal.Addon(name: "snapshot-test");

        for (var body = 0; (body < 2); body++) { fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(index: body), false, Budget: 8, Reach: new(["tap"])), Principal.Console); }
        var runtime = fixture.Server.Input;
        var tick = runtime.Interval.Tick;

        Assert.True(condition: fixture.Server.Body(index: 0)!.Channels.TryGetOrdinal(name: "tap", ordinal: out var address));
        Assert.True(condition: fixture.Server.Population.InputCatalog.TryGetLogicalId(id: out var channel, name: "tap"));
        var held = default(PlayerIntent).WithChannel(ordinal: address, value: FixedQ4816.One);

        Assert.True(condition: runtime.TryPublishSnapshot(actor, 0, "guest", held, tick, out var first, out var press, out var reason), userMessage: reason);
        var submission = new IntentSubmission(tick, 0, press.Image, actor, Input: press, InputOwner: first);
        var writer = new WireWriter();

        Assert.True(condition: WorldWireCodec.TryWriteIntentSubmission(submission: submission, writer: writer));
        var reader = new WireReader(bytes: writer.ToArray());
        var decodedSubmission = WorldWireCodec.ReadIntentSubmission(reader: ref reader);

        Assert.True(condition: reader.TryFinish(failure: out _));
        Assert.Equal(first, decodedSubmission.InputOwner);
        Assert.Equal(actor, decodedSubmission.Principal);
        var bodyZero = fixture.Server.Body(index: 0)!;

        Assert.False(condition: fixture.Server.ApplyIntentSubmission(body: bodyZero, submission: decodedSubmission with { Principal = Principal.Console }).IsAccepted);
        Assert.True(condition: fixture.Server.ApplyIntentSubmission(body: bodyZero, submission: decodedSubmission).IsAccepted);
        Assert.True(condition: runtime.TryPublishSnapshot(actor, 1, "guest", held, tick, out var second, out var other, out reason), userMessage: reason);
        Assert.NotEqual(actual: second, expected: first);
        Assert.Equal(PrincipalKind.Session, first.Kind);
        Assert.True(condition: runtime.TryAdmit(batch: other, bodyIndex: 1, principal: second, reason: out reason), userMessage: reason);
        Assert.True(condition: runtime.IsController(actor: actor, body: 0, principal: first));
        Assert.False(condition: runtime.IsController(actor: actor, body: 1, principal: first));
        Assert.False(condition: runtime.IsController(first, Principal.Console, 0));
        Assert.False(condition: fixture.Server.GrantTable.Allows(first, WorldCapability.Drive, GrantSubject.Body(index: 0)).IsAllowed);
        Assert.True(condition: runtime.TryPublishSnapshot(actor, 0, "guest", default, tick, out var releasing, out var release, out reason), userMessage: reason);
        Assert.Equal(actual: releasing, expected: first);
        Assert.True(condition: runtime.TryAdmit(batch: release, bodyIndex: 0, principal: first, reason: out reason), userMessage: reason);
        Assert.True(condition: runtime.TryGet(input: out var owner, principal: first));
        Assert.True(condition: owner!.TryEvent(channel: channel, kind: WorldInputEventKind.Press, occurrence: 0, record: out var pressed));
        Assert.True(condition: owner.TryEvent(channel: channel, kind: WorldInputEventKind.Release, occurrence: 0, record: out var released));
        Assert.Equal(pressed.Id, released.HoldPress);
        Assert.True(condition: runtime.TryGet(input: out var otherOwner, principal: second));
        Assert.True(condition: otherOwner!.TryHeld(channel: channel, hold: out _));
        Assert.Equal(FixedQ4816.One, fixture.Server.GrantTable.ReadSessionChannel(second.Index, channel));
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!), out var decoded, out reason), userMessage: reason);
        using var restored = Fixtures.FreshServer(definition);

        restored.Server.RestoreCheckpoint(checkpoint: decoded!);
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: tick), WorldStateHashComposition.HashAuthoritative(server: restored.Server, tick: tick));
        Assert.True(condition: restored.Server.Input.TryPublishSnapshot(actor, 1, "guest", held, tick, out var resumed, out var unchanged, out reason), userMessage: reason);
        Assert.Equal(actual: resumed, expected: second);
        Assert.Empty(collection: unchanged.Events.ToArray());
        Assert.True(condition: restored.Server.Input.TryAdmit(batch: unchanged, bodyIndex: 1, principal: resumed, reason: out reason), userMessage: reason);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 1_000); index++) {
            if (!restored.Server.Input.TryPublishSnapshot(actor, 1, "guest", held, tick, out resumed, out unchanged, out reason) ||
                !restored.Server.Input.TryAdmit(batch: unchanged, bodyIndex: 1, principal: resumed, reason: out reason)) { throw new InvalidOperationException(message: reason); }
        }
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before));
        restored.Step();
        tick = restored.Server.Input.Interval.Tick;
        restored.Server.Input.CompleteSnapshots(actor: actor, source: "guest", tick: tick);
        Assert.True(condition: restored.Server.Input.TryGet(input: out otherOwner, principal: second));
        Assert.False(condition: otherOwner!.TryHeld(channel: channel, hold: out _));
        Assert.Equal(FixedQ4816.Zero, restored.Server.GrantTable.ReadSessionChannel(second.Index, channel));
        Assert.True(condition: otherOwner.TryEvent(channel: channel, kind: WorldInputEventKind.Release, occurrence: 0, record: out _));
        Assert.True(condition: restored.Server.Input.TryPublishSnapshot(actor, 1, "guest", held, tick, out resumed, out unchanged, out reason), userMessage: reason);
        Assert.True(condition: restored.Server.Input.TryAdmit(batch: unchanged, bodyIndex: 1, principal: resumed, reason: out reason), userMessage: reason);
        restored.Server.Revoke(new(actor, WorldCapability.Drive, GrantSubject.Body(index: 1), false), Principal.Console);
        restored.Server.Input.RefreshPermissions();
        Assert.True(condition: restored.Server.Input.TryGet(input: out otherOwner, principal: second));
        Assert.False(condition: otherOwner!.TryHeld(channel: channel, hold: out _));
        Assert.True(condition: otherOwner.TryEvent(channel: channel, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out _));
    }
}
