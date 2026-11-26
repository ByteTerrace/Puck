using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class FederatedInputLawTests {
    [Fact]
    public void OrderedPacketsPreserveTransientEdgesAndDisplacementAndRestoreWithTheirPendingQueue() {
        var basis = Fixtures.PeerPopulationDocument(networkPlayers: 1);
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels,
            new("tap", ChannelShape.Binary, Composition: true),
            new("delta", ChannelShape.Relative, Composition: true, Relative: new(MaximumPerStep: 10, Sensitivity: 1, Unit: "units"))],
        };
        using var fixture = Fixtures.FreshServer(definition);
        const int Body = WorldBodiesLimits.LocalSeatCount;

        fixture.Server.Population.ApplyPeerAdmitted(new(Body, 1, IntentSource.Live, Principal.Console, "test", "input", false, null, 0, default, true), []);
        var actor = fixture.Server.Population.PeerPrincipal(index: Body);

        fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(index: Body), false, Budget: 8,
            Reach: new(["tap", "delta"])), Principal.Console);
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var tap, name: "tap"));
        Assert.True(condition: catalog.TryGetLogicalId(id: out var delta, name: "delta"));
        WorldInputSource[] sources = [new(0, tap, WorldInputContributionKind.Level, "test:tap"), new(1, delta, WorldInputContributionKind.Relative, "test:delta")];
        var owner = fixture.Server.Input.Admit(bodyIndex: Body, principal: actor, sources: sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.NextInputTick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        Assert.True(condition: producer.Observe(1, FixedQ3232.FromDouble(value: 0.25).Value, tick, default, 0));
        var press = producer.Publish(tick);

        fixture.Server.PublishFederatedIntent(leaseId: 77, submission: new(tick, Body, press.Image, actor, press));
        Assert.True(condition: producer.Observe(0, 0, tick, FixedQ4816.FromRawBits(value: 2), 1));
        Assert.True(condition: producer.Observe(1, FixedQ3232.FromDouble(value: 0.5).Value, tick, FixedQ4816.FromRawBits(value: 2), 0));
        var release = producer.Publish(tick);

        fixture.Server.PublishFederatedIntent(leaseId: 77, submission: new(tick, Body, release.Image, actor, release));
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!), out var decoded, out reason), userMessage: reason);
        Assert.Equal(2, Assert.Single(collection: decoded!.Server.FederatedInputs).Pending.Length);
        using var restored = Fixtures.FreshServer(definition);

        restored.Server.RestoreCheckpoint(checkpoint: decoded);
        // A peer's grant rows are minted by its admission and are no part of a checkpoint, so the restored authority is
        // given the row its admission would mint.
        restored.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(index: Body), false, Budget: 8, Reach: new(["tap", "delta"])), Principal.Console);
        fixture.Step(); restored.Step();
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: tick), WorldStateHashComposition.HashAuthoritative(server: restored.Server, tick: tick));
        Assert.True(condition: owner.TryEvent(channel: tap, kind: WorldInputEventKind.Press, occurrence: 0, record: out var first));
        Assert.True(condition: owner.TryEvent(channel: tap, kind: WorldInputEventKind.Release, occurrence: 0, record: out var last));
        Assert.Equal(first.Id, last.HoldPress);
        var body = fixture.Server.Body(index: Body)!;

        Assert.Equal(FixedQ3232.FromDouble(value: 0.75), body.ChannelReadComposed.Channels.Relative(ordinal: owner.Mapping.Address(logicalId: delta)));
        Assert.False(condition: owner.TryHeld(channel: tap, hold: out _));
        var sequence = owner.Sequence;

        fixture.Step();
        Assert.Equal(0, body.ChannelReadComposed.Channels.Relative(ordinal: owner.Mapping.Address(logicalId: delta)).Value);
        Assert.Equal(sequence, owner.Sequence);
        tick = fixture.Server.NextInputTick;
        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 2));
        press = producer.Publish(tick);
        fixture.Server.PublishFederatedIntent(leaseId: 77, submission: new(tick, Body, press.Image, actor, press));
        fixture.Step();
        Assert.True(condition: owner.TryHeld(channel: tap, hold: out var held));
        fixture.Server.ReleaseFederatedIntents(leaseId: 77);
        Assert.False(condition: owner.TryHeld(channel: tap, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: tap, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var cancelled));
        Assert.Equal(held.Press, cancelled.HoldPress);
    }

    // A link that stalls and then delivers a burst hands one tick the packets of many. The authority admits what the
    // owner's step has room for and holds the rest, in order, for the ticks after: no packet is refused, the owner keeps
    // its epoch, and every edge lands.
    [Fact]
    public void BunchedPacketsOverTheStepBoundSpreadOverLaterTicksWithoutRestartingTheOwner() {
        const int StepCapacity = 4;
        const int Packets = 6;
        var basis = Fixtures.PeerPopulationDocument(networkPlayers: 1);
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("tap", ChannelShape.Binary, Composition: true)],
            InputHistory = new(EventCapacity: 64, MaximumEventsPerStep: StepCapacity),
        };
        using var fixture = Fixtures.FreshServer(definition);
        const int Body = WorldBodiesLimits.LocalSeatCount;

        fixture.Server.Population.ApplyPeerAdmitted(new(Body, 1, IntentSource.Live, Principal.Console, "test", "input", false, null, 0, default, true), []);
        var actor = fixture.Server.Population.PeerPrincipal(index: Body);

        fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(index: Body), false, Budget: 8, Reach: new(["tap"])), Principal.Console);
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var tap, name: "tap"));
        WorldInputSource[] sources = [new(0, tap, WorldInputContributionKind.Level, "test:tap")];
        var owner = fixture.Server.Input.Admit(bodyIndex: Body, principal: actor, sources: sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, StepCapacity, owner.Revision, owner.Epoch);
        var epoch = owner.Epoch;
        var tick = fixture.Server.NextInputTick;

        // Each packet is a press and a release, two events; together they carry three steps' worth.
        for (var packet = 0; (packet < Packets); packet++) {
            Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, FixedQ4816.FromRawBits(value: ((2 * packet) + 1)), ((ulong)(packet + 1))));
            Assert.True(condition: producer.Observe(0, 0, tick, FixedQ4816.FromRawBits(value: ((2 * packet) + 2)), ((ulong)(packet + 1))));
            var batch = producer.Publish(tick);

            fixture.Server.PublishFederatedIntent(leaseId: 91, submission: new(tick, Body, batch.Image, actor, batch));
        }
        for (var step = 1; (step <= (((2 * Packets) + StepCapacity - 1) / StepCapacity)); step++) {
            fixture.Step();
            Assert.Equal(epoch, owner.Epoch);
            Assert.Equal(0UL, owner.RefusedBatches);
            Assert.Equal(((uint)Math.Min(val1: (2 * Packets), val2: (step * StepCapacity))), owner.Sequence);
        }
        Assert.True(condition: owner.TryEvent(channel: tap, kind: WorldInputEventKind.Press, occurrence: (Packets - 1), record: out _));
        Assert.False(condition: owner.TryEvent(channel: tap, kind: WorldInputEventKind.Press, occurrence: Packets, record: out _));
        Assert.False(condition: owner.TryHeld(channel: tap, hold: out _));
    }
}
