using Puck.Commands;
using Puck.Maths;
using Puck.Networking;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class InputPermissionTests {
    [Theory]
    [InlineData(0)]
    [InlineData(WorldBodiesLimits.LocalSeatCount)]
    public void ACoDriverCannotReplaceTheOccupantsMappingOrClearItsHeldImage(int bodyIndex) {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            PopulationRaw = basis.Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 1, NetworkPlayers = 1, DefaultPeerSourceRaw = IntentSource.Idle },
            ChannelsRaw = [.. basis.Channels, new("a", ChannelShape.Binary, Composition: true), new("b", ChannelShape.Binary, Composition: true)],
            SeatModesRaw = [new(Name: "focus", DefaultState: "a", States: [new("a"), new("b")])],
            InputModes = new([], [new("focus", new(WorldInputModeSelectorKind.SeatState, "focus"), "a", [
                new("a", ["a"], ["a"]), new("b", ["b"], ["b"]),
            ])]),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        if (bodyIndex == 0) { fixture.JoinSeat(); }
        else { Assert.Equal(1, fixture.Server.Population.SetSimulatedCount(1)); }
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;
        Assert.True(catalog.TryGetLogicalId("a", out var a));
        var occupant = bodyIndex == 0 ? Principal.Seat(0) : fixture.Server.Population.PeerPrincipal(bodyIndex);
        var driver = Principal.Seat(1);
        fixture.Server.Grant(new(occupant, WorldCapability.Drive, GrantSubject.Body(bodyIndex), false, Budget: 8, Reach: new(["a", "b"])), Principal.Console);
        fixture.Server.Grant(new(driver, WorldCapability.Drive, GrantSubject.Body(bodyIndex), false), Principal.Console);
        WorldInputSource[] sources = [new(0, a, WorldInputContributionKind.Level, "key:a")];
        var primary = fixture.Server.Input.Admit(occupant, bodyIndex, sources);
        var producer = new WorldInputProducer(catalog, primary.Mapping, sources, 128, primary.Revision, primary.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        var body = fixture.Server.Body(bodyIndex)!;
        Assert.True(fixture.Server.ApplyIntentSubmission(body, new(tick, bodyIndex, default, occupant, Input: producer.Publish(tick))).IsAccepted);
        var secondary = fixture.Server.Input.Admit(driver, bodyIndex, []);
        Assert.True(fixture.Server.Input.TrySetSeatState(driver, "focus", "b", 0, out var reason), reason);
        Assert.NotEqual(primary.Mapping.Id, secondary.Mapping.Id);
        fixture.Server.Input.Reset(driver);
        Assert.True(body.Channels.TryGetOrdinal("a", out var address));
        Assert.False(body.Channels.TryGetOrdinal("b", out _));
        Assert.Equal(FixedQ4816.One, body.TakeSubmittedInput().HeldChannels[address]);
        Assert.True(primary.TryHeld(a, out _));
    }

    [Fact]
    public void NarrowingReachCancelsHistoryAndHeldInputBeforeARegrant() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with { ChannelsRaw = [.. basis.Channels,
            new("restrictedHold", ChannelShape.Binary, Composition: true), new("restrictedText", ChannelShape.Text, Composition: true)] };
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.JoinSeat();
        fixture.Step();
        var principal = Principal.Addon("input-permission");
        var grant = new WorldGrant(principal, WorldCapability.Drive, GrantSubject.Body(0), false,
            Budget: 8, Reach: new(["restrictedHold", "restrictedText"]));
        fixture.Server.Grant(grant, Principal.Console);
        var catalog = fixture.Server.Population.InputCatalog;
        Assert.True(catalog.TryGetLogicalId("restrictedHold", out var hold));
        Assert.True(catalog.TryGetLogicalId("restrictedText", out var text));
        WorldInputSource[] sources = [new(0, hold, WorldInputContributionKind.Level, "held")];
        var owner = fixture.Server.Input.Admit(principal, 0, sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        Assert.True(producer.Text(text, "a", tick, FixedQ4816.FromRawBits(1)));
        Assert.True(fixture.Server.Input.TryAdmit(principal, producer.Publish(tick), out var reason), reason);
        Assert.True(owner.TryHeld(hold, out var origin));
        Assert.True(owner.TryEvent(text, WorldInputEventKind.Text, 0, out _));
        fixture.Server.Grant(grant with { Reach = new(["restrictedText"]) }, Principal.Console);
        fixture.Server.Input.RefreshPermissions();
        Assert.False(owner.TryHeld(hold, out _));
        Assert.True(owner.TryEvent(hold, WorldInputEventKind.Cancel, 0, out var cancelled));
        Assert.Equal(origin.Press, cancelled.HoldPress);
        Assert.False(owner.TryEvent(text, WorldInputEventKind.Text, 0, out _));
        var epoch = owner.Epoch;
        fixture.Server.Input.RefreshPermissions();
        Assert.Equal(epoch, owner.Epoch);
        producer.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        Assert.True(producer.Observe(0, 0, tick, default, 1));
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, FixedQ4816.FromRawBits(2), 2));
        Assert.False(fixture.Server.Input.TryAdmit(principal, producer.Publish(tick), out reason));
        Assert.Contains("Drive reach excludes 'restrictedHold'", reason);
        fixture.Server.Grant(grant, Principal.Console);
        Assert.False(owner.TryHeld(hold, out _));
        Assert.True(owner.Sources[0].AwaitingNeutral);
    }

    [Fact]
    public void AGrantFollowsItsLogicalActionAcrossReusedSlotsAndCheckpointRestore() {
        var catalog = WorldInputCatalog.Compile([
            new("a", ChannelShape.Binary), new("b", ChannelShape.Binary)
        ], new([], [new("focus", new(WorldInputModeSelectorKind.SeatState, "focus"), "a", [
            new("a", ["a"]), new("b", ["b"])
        ])]));
        var a = Assert.Single(catalog.Mappings.ToArray(), mapping => mapping.Name == "focus=a");
        var b = Assert.Single(catalog.Mappings.ToArray(), mapping => mapping.Name == "focus=b");
        Assert.Equal(a.Address(0), b.Address(1));
        var writer = new WireWriter();
        WorldWireCodec.WriteChannelSelection(writer, new(["a"]));
        var reader = new WireReader(writer.ToArray());
        var selection = WorldWireCodec.ReadChannelSelection(ref reader);
        Assert.True(reader.TryFinish(out _));
        var principal = Principal.Addon("guest");
        var subject = GrantSubject.Body(0);
        var grants = new WorldGrants(population: 4, seatCount: 0, routeTransition: static (_, _, _) => { });
        Assert.True(grants.TryGrant(new(principal, WorldCapability.Drive, subject, false, Budget: 8, Reach: selection), out var reason), reason);
        Assert.True(grants.TryGetChannelReach(principal, subject, a.Channels, out var active));
        Assert.True(active.Contains(a.Address(0)));
        Assert.True(grants.TryGetChannelReach(principal, subject, b.Channels, out var inactive));
        Assert.True(inactive.IsEmpty);
        var restored = new WorldGrants(population: 4, seatCount: 0, routeTransition: static (_, _, _) => { });
        restored.Restore(grants.Capture());
        Assert.True(restored.TryGetChannelReach(principal, subject, b.Channels, out inactive));
        Assert.True(inactive.IsEmpty);
        Assert.True(restored.TryGetChannelReach(principal, subject, a.Channels, out active));
        Assert.True(active.Contains(a.Address(0)));
    }
}
