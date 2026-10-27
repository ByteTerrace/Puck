using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class FederatedInputLawTests {
    [Fact]
    public void OrderedPacketsPreserveTransientEdgesAndDisplacementAndRestoreWithTheirPendingQueue() {
        var basis = Fixtures.PeerPopulationDocument(networkPlayers: 1);
        var definition = basis with { ChannelsRaw = [.. basis.Channels,
            new("tap", ChannelShape.Binary, Composition: true),
            new("delta", ChannelShape.Relative, Composition: true, Relative: new(MaximumPerStep: 10, Sensitivity: 1, Unit: "units"))] };
        using var fixture = Fixtures.FreshServer(definition);
        const int Body = WorldBodiesLimits.LocalSeatCount;
        fixture.Server.Population.ApplyPeerAdmitted(new(Body, 1, IntentSource.Live, Principal.Console, "test", "input", false, null, 0, default), []);
        var actor = fixture.Server.Population.PeerPrincipal(Body);
        fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(Body), false, Budget: 8,
            Reach: new(["tap", "delta"])), Principal.Console);
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;
        Assert.True(catalog.TryGetLogicalId("tap", out var tap));
        Assert.True(catalog.TryGetLogicalId("delta", out var delta));
        WorldInputSource[] sources = [new(0, tap, WorldInputContributionKind.Level, "test:tap"), new(1, delta, WorldInputContributionKind.Relative, "test:delta")];
        var owner = fixture.Server.Input.Admit(actor, Body, sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.NextInputTick;
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        Assert.True(producer.Observe(1, FixedQ3232.FromDouble(0.25).Value, tick, default, 0));
        var press = producer.Publish(tick);
        fixture.Server.PublishFederatedIntent(77, new(tick, Body, press.Image, actor, press));
        Assert.True(producer.Observe(0, 0, tick, FixedQ4816.FromRawBits(2), 1));
        Assert.True(producer.Observe(1, FixedQ3232.FromDouble(0.5).Value, tick, FixedQ4816.FromRawBits(2), 0));
        var release = producer.Publish(tick);
        fixture.Server.PublishFederatedIntent(77, new(tick, Body, release.Image, actor, release));
        Assert.True(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), reason);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint!), out var decoded, out reason), reason);
        Assert.Equal(2, Assert.Single(decoded!.Server.FederatedInputs).Pending.Length);
        using var restored = Fixtures.FreshServer(definition);
        restored.Server.RestoreCheckpoint(decoded);
        fixture.Step(); restored.Step();
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(fixture.Server, tick), WorldStateHashComposition.HashAuthoritative(restored.Server, tick));
        Assert.True(owner.TryEvent(tap, WorldInputEventKind.Press, 0, out var first));
        Assert.True(owner.TryEvent(tap, WorldInputEventKind.Release, 0, out var last));
        Assert.Equal(first.Id, last.HoldPress);
        var body = fixture.Server.Body(Body)!;
        Assert.Equal(FixedQ3232.FromDouble(0.75), body.ChannelReadComposed.Channels.Relative(owner.Mapping.Address(delta)));
        Assert.False(owner.TryHeld(tap, out _));
        var sequence = owner.Sequence;
        fixture.Step();
        Assert.Equal(0, body.ChannelReadComposed.Channels.Relative(owner.Mapping.Address(delta)).Value);
        Assert.Equal(sequence, owner.Sequence);
        tick = fixture.Server.NextInputTick;
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, default, 2));
        press = producer.Publish(tick);
        fixture.Server.PublishFederatedIntent(77, new(tick, Body, press.Image, actor, press));
        fixture.Step();
        Assert.True(owner.TryHeld(tap, out var held));
        fixture.Server.ReleaseFederatedIntents(77);
        Assert.False(owner.TryHeld(tap, out _));
        Assert.True(owner.TryEvent(tap, WorldInputEventKind.Cancel, 0, out var cancelled));
        Assert.Equal(held.Press, cancelled.HoldPress);
    }
}
