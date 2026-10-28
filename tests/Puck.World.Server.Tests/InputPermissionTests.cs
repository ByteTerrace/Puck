using Puck.Commands;
using Puck.Maths;
using Puck.Networking;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class InputPermissionTests {
    [InlineData(0)]
    [InlineData(WorldBodiesLimits.LocalSeatCount)]
    [Theory]
    public void ACoDriverCannotReplaceTheOccupantsMappingOrClearItsHeldImage(int bodyIndex) {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            PopulationRaw = basis.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1), NetworkPlayers = 1, DefaultPeerSourceRaw = IntentSource.Idle },
            ChannelsRaw = [.. basis.Channels, new("a", ChannelShape.Binary, Composition: true), new("b", ChannelShape.Binary, Composition: true)],
            SeatModesRaw = [new(Name: "focus", DefaultState: "a", States: [new("a"), new("b")])],
            InputModes = new([], [new("focus", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "focus"), "a", [
                new("a", ["a"], ["a"]), new("b", ["b"], ["b"]),
            ])]),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        if (bodyIndex == 0) { fixture.JoinSeat(); } else { Assert.Equal(1, fixture.Server.Population.SetSimulatedCount(1)); }
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var a, name: "a"));
        var occupant = ((bodyIndex == 0) ? Principal.Seat(slot: 0) : fixture.Server.Population.PeerPrincipal(index: bodyIndex));
        var driver = Principal.Seat(slot: 1);

        fixture.Server.Grant(new(occupant, WorldCapability.Drive, GrantSubject.Body(index: bodyIndex), false, Budget: 8, Reach: new(["a", "b"])), Principal.Console);
        fixture.Server.Grant(new(driver, WorldCapability.Drive, GrantSubject.Body(index: bodyIndex), false), Principal.Console);
        WorldInputSource[] sources = [new(0, a, WorldInputContributionKind.Level, "key:a")];
        var primary = fixture.Server.Input.Admit(bodyIndex: bodyIndex, principal: occupant, sources: sources);
        var producer = new WorldInputProducer(catalog, primary.Mapping, sources, 128, primary.Revision, primary.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        var body = fixture.Server.Body(index: bodyIndex)!;

        Assert.True(condition: fixture.Server.ApplyIntentSubmission(body: body, submission: new(tick, bodyIndex, default, occupant, Input: producer.Publish(tick))).IsAccepted);
        var secondary = fixture.Server.Input.Admit(bodyIndex: bodyIndex, principal: driver, sources: []);

        Assert.True(condition: fixture.Server.Input.TrySetSeatState(driver, "focus", "b", 0, out var reason), userMessage: reason);
        Assert.NotEqual(primary.Mapping.Id, secondary.Mapping.Id);
        fixture.Server.Input.Reset(driver);
        Assert.True(condition: body.Channels.TryGetOrdinal(name: "a", ordinal: out var address));
        Assert.False(condition: body.Channels.TryGetOrdinal(name: "b", ordinal: out _));
        Assert.Equal(FixedQ4816.One, body.TakeSubmittedInput().HeldChannels[address]);
        Assert.True(condition: primary.TryHeld(channel: a, hold: out _));
    }
    [Fact]
    public void NarrowingReachCancelsHistoryAndHeldInputBeforeARegrant() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels,
            new("restrictedHold", ChannelShape.Binary, Composition: true), new("restrictedText", ChannelShape.Text, Composition: true)],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.JoinSeat();
        fixture.Step();
        var principal = Principal.Addon(name: "input-permission");
        var grant = new WorldGrant(principal, WorldCapability.Drive, GrantSubject.Body(index: 0), false,
            Budget: 8, Reach: new(["restrictedHold", "restrictedText"]));

        fixture.Server.Grant(grant, Principal.Console);
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var hold, name: "restrictedHold"));
        Assert.True(condition: catalog.TryGetLogicalId(id: out var text, name: "restrictedText"));
        WorldInputSource[] sources = [new(0, hold, WorldInputContributionKind.Level, "held")];
        var owner = fixture.Server.Input.Admit(bodyIndex: 0, principal: principal, sources: sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        Assert.True(condition: producer.Text(text, "a", tick, FixedQ4816.FromRawBits(value: 1)));
        Assert.True(condition: fixture.Server.Input.TryAdmit(principal, producer.Publish(tick), out var reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: hold, hold: out var origin));
        Assert.True(condition: owner.TryEvent(channel: text, kind: WorldInputEventKind.Text, occurrence: 0, record: out _));
        fixture.Server.Grant(grant with { Reach = new(["restrictedText"]) }, Principal.Console);
        fixture.Server.Input.RefreshPermissions();
        Assert.False(condition: owner.TryHeld(channel: hold, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: hold, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var cancelled));
        Assert.Equal(origin.Press, cancelled.HoldPress);
        Assert.False(condition: owner.TryEvent(channel: text, kind: WorldInputEventKind.Text, occurrence: 0, record: out _));
        var epoch = owner.Epoch;

        fixture.Server.Input.RefreshPermissions();
        Assert.Equal(epoch, owner.Epoch);
        producer.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        Assert.True(condition: producer.Observe(0, 0, tick, default, 1));
        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, FixedQ4816.FromRawBits(value: 2), 2));
        Assert.False(condition: fixture.Server.Input.TryAdmit(principal, producer.Publish(tick), out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "Drive reach excludes 'restrictedHold'");
        fixture.Server.Grant(grant, Principal.Console);
        Assert.False(condition: owner.TryHeld(channel: hold, hold: out _));
        Assert.True(condition: owner.Sources[0].AwaitingNeutral);
    }
    [Fact]
    public void AGrantFollowsItsLogicalActionAcrossReusedSlotsAndCheckpointRestore() {
        var catalog = WorldInputCatalog.Compile(declarations: [
            new("a", ChannelShape.Binary), new("b", ChannelShape.Binary)
        ], modes: new([], [new("focus", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "focus"), "a", [
            new("a", ["a"]), new("b", ["b"])
        ])]));
        var a = Assert.Single(collection: catalog.Mappings.ToArray(), predicate: mapping => (mapping.Name == "focus=a"));
        var b = Assert.Single(collection: catalog.Mappings.ToArray(), predicate: mapping => (mapping.Name == "focus=b"));

        Assert.Equal(a.Address(logicalId: 0), b.Address(logicalId: 1));
        var writer = new WireWriter();

        WorldWireCodec.WriteChannelSelection(writer, new(["a"]));
        var reader = new WireReader(bytes: writer.ToArray());
        var selection = WorldWireCodec.ReadChannelSelection(reader: ref reader);

        Assert.True(condition: reader.TryFinish(failure: out _));
        var principal = Principal.Addon(name: "guest");
        var subject = GrantSubject.Body(index: 0);
        var grants = new WorldGrants(population: 4, seatCount: 0, routeTransition: static (_, _, _) => { });

        Assert.True(condition: grants.TryGrant(grant: new(principal, WorldCapability.Drive, subject, false, Budget: 8, Reach: selection), reason: out var reason), userMessage: reason);
        Assert.True(condition: grants.TryGetChannelReach(principal, subject, a.Channels, out var active));
        Assert.True(condition: active.Contains(ordinal: a.Address(logicalId: 0)));
        Assert.True(condition: grants.TryGetChannelReach(principal, subject, b.Channels, out var inactive));
        Assert.True(condition: inactive.IsEmpty);
        var restored = new WorldGrants(population: 4, seatCount: 0, routeTransition: static (_, _, _) => { });

        restored.Restore(checkpoint: grants.Capture());
        Assert.True(condition: restored.TryGetChannelReach(principal, subject, b.Channels, out inactive));
        Assert.True(condition: inactive.IsEmpty);
        Assert.True(condition: restored.TryGetChannelReach(principal, subject, a.Channels, out active));
        Assert.True(condition: active.Contains(ordinal: a.Address(logicalId: 0)));
    }
}
