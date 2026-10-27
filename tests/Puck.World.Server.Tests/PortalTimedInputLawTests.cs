using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class PortalTimedInputLawTests {
    [Fact]
    public void APortalPreservesSameTickEdgesAndTextAndCancelsALostRoute() {
        var basis = Fixtures.BuildDocument();
        var document = basis with {
            ChannelsRaw = [.. basis.Channels, new("portalPulse", ChannelShape.Binary, Composition: true),
                new("portalText", ChannelShape.Text, Composition: true)],
            Admission = [new(WorldAdmissionEntry.AnyAuthority, WorldAdmissionTrustMode.FederatedAuthority,
                Grants: [new(WorldCapability.Observe, Budget: 64)])],
        };
        using var source = Fixtures.FreshServer(definition: document);
        source.JoinSeat();
        source.Step();
        using var destination = HostRow.Build("portal-destination", document with {
            ChannelsRaw = [.. document.Channels, new("aaTargetOnly", ChannelShape.Binary, Composition: true)],
        });
        Assert.True(destination.Server.TryAdmitSession("portal-source", out var session, out _, out var refusal), refusal);
        var bridge = new WorldPortalInputProducer(destination.Instance, session);
        destination.Server.Advance(Fixtures.StepTicks);
        var catalog = source.Server.Population.InputCatalog;
        Assert.True(catalog.TryGetLogicalId("portalPulse", out var pulse));
        Assert.True(catalog.TryGetLogicalId("portalText", out var text));
        var principal = Principal.Seat(0);
        WorldInputSource[] profile = [new(0, pulse, WorldInputContributionKind.Level, "pulse")];
        var owner = source.Server.Input.Admit(principal, 0, profile);
        var producer = new WorldInputProducer(catalog, owner.Mapping, profile, 128, owner.Revision, owner.Epoch);
        var tick = source.Server.NextInputTick;
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, FixedQ4816.FromRawBits(1), 1));
        Assert.True(producer.Text(text, "ab", tick, FixedQ4816.FromRawBits(2)));
        Assert.True(producer.Observe(0, 0, tick, FixedQ4816.FromRawBits(3), 1));
        source.Server.EnqueueIntent(new(tick, 0, default, principal, Input: producer.Publish(tick)));
        source.Step();
        var forward = new WorldPortalForward(0, principal, default, default, default, owner.Mapping.Channels);
        Assert.True(bridge.Forward(destination.Instance, source.Server, in forward, default));
        destination.Server.Advance(Fixtures.StepTicks);
        Assert.True(destination.Server.Input.TryGet(session, out var received));
        var targetCatalog = destination.Server.Population.InputCatalog;
        Assert.True(targetCatalog.TryGetLogicalId("portalPulse", out var targetPulse));
        Assert.True(targetCatalog.TryGetLogicalId("portalText", out var targetText));
        Assert.NotEqual(pulse, targetPulse);
        Assert.True(received!.TryEvent(targetPulse, WorldInputEventKind.Press, 0, out var press));
        Assert.True(received.TryEvent(targetPulse, WorldInputEventKind.Release, 0, out var release));
        Assert.Equal(press.Id, release.HoldPress);
        Assert.Equal(press.Admission.Tick, release.Admission.Tick);
        Assert.False(received.TryHeld(targetPulse, out _));
        var textValues = Enumerable.Range(0, received.Count).Select(received.Record)
            .Where(row => row.Event.Channel == targetText && row.Event.Kind == WorldInputEventKind.Text)
            .Select(row => row.Event.RawValue).ToArray();
        Assert.Equal(new long[] { 'a', 'b' }, textValues);

        tick = source.Server.NextInputTick;
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, default, 2));
        source.Server.EnqueueIntent(new(tick, 0, default, principal, Input: producer.Publish(tick)));
        source.Step();
        var mapped = default(PlayerIntent).WithChannel(received.Mapping.Address(targetPulse), FixedQ4816.One);
        Assert.True(bridge.Forward(destination.Instance, source.Server, in forward, mapped));
        destination.Server.Advance(Fixtures.StepTicks);
        Assert.True(received.TryHeld(targetPulse, out var held));
        bridge.Stop(destination.Instance);
        destination.Server.Advance(Fixtures.StepTicks);
        Assert.False(received.TryHeld(targetPulse, out _));
        Assert.False(received.TryEvent(targetPulse, WorldInputEventKind.Release, 0, out _));
        Assert.True(received.TryEvent(targetPulse, WorldInputEventKind.Cancel, 0, out var cancelled));
        Assert.Equal(held.Press, cancelled.HoldPress);
    }
}
