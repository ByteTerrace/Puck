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
            InputModes = new([.. basis.Channels.Select(row => row.Name), "routeHold"], [
                new("focus", new(WorldInputModeSelectorKind.SeatState, "focus"), "source", [
                    new("source", [], ["source"]), new("target", ["aaTargetOnly"], ["target"]),
                ]),
            ]),
        };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.JoinSeat(0);
        fixture.JoinSeat(1);
        fixture.Step();
        var source = Principal.Seat(0);
        fixture.Server.Grant(new(source, WorldCapability.Drive, GrantSubject.Body(1), false), Principal.Console);
        fixture.Server.Grant(new(source, WorldCapability.Control, GrantSubject.Body(1), false), Principal.Console);
        Assert.True(fixture.Server.Engagement.Compose(0, GrantSubject.Body(1), false, source, source));
        var catalog = fixture.Server.Population.InputCatalog;
        Assert.True(catalog.TryGetLogicalId("routeHold", out var channel));
        WorldInputSource[] profile = [new(0, channel, WorldInputContributionKind.Level, "route-key")];
        var owner = fixture.Server.Input.Admit(source, 0, profile);
        var targetOwner = fixture.Server.Input.Admit(Principal.Seat(1), 1, []);
        Assert.True(fixture.Server.Input.TrySetSeatState(Principal.Seat(1), "focus", "target", 0, out var reason), reason);
        Assert.NotEqual(owner.Mapping.Id, targetOwner.Mapping.Id);
        var producer = new WorldInputProducer(catalog, owner.Mapping, profile, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.NextInputTick;
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        fixture.Server.EnqueueIntent(new(tick, 0, default, source, Input: producer.Publish(tick)));
        fixture.Step();
        Assert.True(owner.TryHeld(channel, out var held));
        Assert.True(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out reason), reason);
        Assert.Single(checkpoint!.Server.RoutedInputs);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint), out var decoded, out reason), reason);
        using var restored = Fixtures.FreshServer(definition);
        restored.Server.RestoreCheckpoint(decoded!);
        var before = WorldStateHashComposition.HashAuthoritative(fixture.Server, tick);
        Assert.Equal(before, WorldStateHashComposition.HashAuthoritative(restored.Server, tick));
        fixture.Step();
        restored.Step();
        Assert.Equal(FixedQ4816.One, fixture.Server.Body(1)!.ReadChannelScalar(targetOwner.Mapping.Address(channel)));
        Assert.Equal(held, AssertHeld(owner, channel));
        Assert.Equal(0, targetOwner.Count);
        foreach (var component in WorldStateHashComposition.Authoritative) {
            Assert.True(WorldStateHashComposition.Compose(fixture.Server, tick + 1, 0, [component]) ==
                WorldStateHashComposition.Compose(restored.Server, tick + 1, 0, [component]), component.ToString());
        }
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(fixture.Server, tick + 1),
            WorldStateHashComposition.HashAuthoritative(restored.Server, tick + 1));
    }

    private static WorldInputHold AssertHeld(WorldInputOwner owner, ushort channel) {
        Assert.True(owner.TryHeld(channel, out var held));
        return held;
    }
}
