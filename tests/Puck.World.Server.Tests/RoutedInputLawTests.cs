using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class RoutedInputLawTests {
    [Fact]
    public void BodyRoutingUsesLogicalNamesWithoutReadmittingHistoryAndRestoresItsPendingFold() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("routeHold", ChannelShape.Binary, Composition: true),
                new("aaTargetOnly", ChannelShape.Binary, Composition: true)],
            SeatModesRaw = [new(Name: "focus", DefaultState: "source", States: [new("source"), new("target")])],
            InputModes = new([.. basis.Channels.Select(selector: row => row.Name), "routeHold"], [
                new("focus", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "focus"), "source", [
                    new("source", [], ["source"]), new("target", ["aaTargetOnly"], ["target"]),
                ]),
            ]),
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat(slot: 0);
        fixture.JoinSeat(slot: 1);
        fixture.Step();
        var source = Principal.Seat(slot: 0);

        fixture.Server.Grant(new(source, WorldCapability.Drive, GrantSubject.Body(index: 1), false), Principal.Console);
        fixture.Server.Grant(new(source, WorldCapability.Control, GrantSubject.Body(index: 1), false), Principal.Console);
        Assert.True(condition: fixture.Server.Engagement.Compose(0, GrantSubject.Body(index: 1), false, source, source));
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var channel, name: "routeHold"));
        WorldInputSource[] profile = [new(0, channel, WorldInputContributionKind.Level, "route-key")];
        var owner = fixture.Server.Input.Admit(bodyIndex: 0, principal: source, sources: profile);
        var targetOwner = fixture.Server.Input.Admit(Principal.Seat(slot: 1), 1, []);

        Assert.True(condition: fixture.Server.Input.TrySetSeatState(Principal.Seat(slot: 1), "focus", "target", 0, out var reason), userMessage: reason);
        Assert.NotEqual(owner.Mapping.Id, targetOwner.Mapping.Id);
        var producer = new WorldInputProducer(catalog, owner.Mapping, profile, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.NextInputTick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        fixture.Server.EnqueueIntent(submission: new(tick, 0, default, source, Input: producer.Publish(tick)));
        fixture.Step();
        Assert.True(condition: owner.TryHeld(channel: channel, hold: out var held));
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out reason), userMessage: reason);
        Assert.Single(collection: checkpoint!.Server.RoutedInputs);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint), out var decoded, out reason), userMessage: reason);
        using var restored = Fixtures.FreshServer(definition);

        restored.Server.RestoreCheckpoint(checkpoint: decoded!);
        var before = WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: tick);

        Assert.Equal(before, WorldStateHashComposition.HashAuthoritative(server: restored.Server, tick: tick));
        fixture.Step();
        restored.Step();
        Assert.Equal(FixedQ4816.One, fixture.Server.Body(index: 1)!.ReadChannelScalar(ordinal: targetOwner.Mapping.Address(logicalId: channel)));
        Assert.Equal(held, AssertHeld(channel: channel, owner: owner));
        Assert.Equal(0, targetOwner.Count);
        foreach (var component in WorldStateHashComposition.Authoritative) {
            Assert.True(condition: (WorldStateHashComposition.Compose(fixture.Server, (tick + 1), 0, [component]) ==
                WorldStateHashComposition.Compose(restored.Server, (tick + 1), 0, [component])), userMessage: component.ToString());
        }
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: (tick + 1)),
            WorldStateHashComposition.HashAuthoritative(server: restored.Server, tick: (tick + 1)));
    }

    private static WorldInputHold AssertHeld(WorldInputOwner owner, ushort channel) {
        Assert.True(condition: owner.TryHeld(channel: channel, hold: out var held));
        return held;
    }
}
