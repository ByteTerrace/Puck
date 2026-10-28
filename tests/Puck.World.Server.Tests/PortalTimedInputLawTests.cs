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

        Assert.True(condition: destination.Server.TryAdmitSession(refusal: out var refusal, session: out var session, sourceAuthority: "portal-source", tier: out _), userMessage: refusal);
        var bridge = new WorldPortalInputProducer(destination: destination.Instance, session: session);

        destination.Server.Advance(stepTicks: Fixtures.StepTicks);
        var catalog = source.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var pulse, name: "portalPulse"));
        Assert.True(condition: catalog.TryGetLogicalId(id: out var text, name: "portalText"));
        var principal = Principal.Seat(slot: 0);
        WorldInputSource[] profile = [new(0, pulse, WorldInputContributionKind.Level, "pulse")];
        var owner = source.Server.Input.Admit(bodyIndex: 0, principal: principal, sources: profile);
        var producer = new WorldInputProducer(catalog, owner.Mapping, profile, 128, owner.Revision, owner.Epoch);
        var tick = source.Server.NextInputTick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, FixedQ4816.FromRawBits(value: 1), 1));
        Assert.True(condition: producer.Text(text, "ab", tick, FixedQ4816.FromRawBits(value: 2)));
        Assert.True(condition: producer.Observe(0, 0, tick, FixedQ4816.FromRawBits(value: 3), 1));
        source.Server.EnqueueIntent(submission: new(tick, 0, default, principal, Input: producer.Publish(tick)));
        source.Step();
        var forward = new WorldPortalForward(0, principal, default, default, default, owner.Mapping.Channels);

        Assert.True(condition: bridge.Forward(destination.Instance, source.Server, in forward, default));
        destination.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.True(condition: destination.Server.Input.TryGet(input: out var received, principal: session));
        var targetCatalog = destination.Server.Population.InputCatalog;

        Assert.True(condition: targetCatalog.TryGetLogicalId(id: out var targetPulse, name: "portalPulse"));
        Assert.True(condition: targetCatalog.TryGetLogicalId(id: out var targetText, name: "portalText"));
        Assert.NotEqual(actual: targetPulse, expected: pulse);
        Assert.True(condition: received!.TryEvent(channel: targetPulse, kind: WorldInputEventKind.Press, occurrence: 0, record: out var press));
        Assert.True(condition: received.TryEvent(channel: targetPulse, kind: WorldInputEventKind.Release, occurrence: 0, record: out var release));
        Assert.Equal(press.Id, release.HoldPress);
        Assert.Equal(press.Admission.Tick, release.Admission.Tick);
        Assert.False(condition: received.TryHeld(channel: targetPulse, hold: out _));
        var textValues = Enumerable.Range(0, received.Count).Select(selector: received.Record)
            .Where(predicate: row => ((row.Event.Channel == targetText) && (row.Event.Kind == WorldInputEventKind.Text)))
            .Select(selector: row => row.Event.RawValue).ToArray();

        Assert.Equal(actual: textValues, expected: new long[] { 'a', 'b' });

        tick = source.Server.NextInputTick;
        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 2));
        source.Server.EnqueueIntent(submission: new(tick, 0, default, principal, Input: producer.Publish(tick)));
        source.Step();
        var mapped = default(PlayerIntent).WithChannel(ordinal: received.Mapping.Address(logicalId: targetPulse), value: FixedQ4816.One);

        Assert.True(condition: bridge.Forward(destination.Instance, source.Server, in forward, mapped));
        destination.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.True(condition: received.TryHeld(channel: targetPulse, hold: out var held));
        bridge.Stop(destination: destination.Instance);
        destination.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.False(condition: received.TryHeld(channel: targetPulse, hold: out _));
        Assert.False(condition: received.TryEvent(channel: targetPulse, kind: WorldInputEventKind.Release, occurrence: 0, record: out _));
        Assert.True(condition: received.TryEvent(channel: targetPulse, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var cancelled));
        Assert.Equal(held.Press, cancelled.HoldPress);
    }
}
