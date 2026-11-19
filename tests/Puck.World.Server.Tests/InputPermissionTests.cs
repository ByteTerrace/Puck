using Puck.Commands;
using Puck.Maths;
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
        // A network body's occupant holds grants only as an admitted peer; a simulated peer is no session and holds none.
        var peer = default(Principal);
        using var fixture = ((bodyIndex == 0) ? Fixtures.FreshServer(definition: definition) : Fixtures.FreshServerWithPeer(definition, out peer));

        if (bodyIndex == 0) { fixture.JoinSeat(); } else { bodyIndex = peer.Index; }
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var a, name: "a"));
        var occupant = ((bodyIndex == 0) ? Principal.Seat(slot: 0) : peer);
        var driver = Principal.Seat(slot: 1);

        fixture.Server.Grant(new(occupant, WorldCapability.Drive, GrantSubject.Body(index: bodyIndex), false, Budget: 8, Reach: new(["a", "b"])), Principal.Console);
        fixture.Server.Grant(new(driver, WorldCapability.Drive, GrantSubject.Body(index: bodyIndex), false, Budget: 8), Principal.Console);
        WorldInputSource[] sources = [new(0, a, WorldInputContributionKind.Level, "key:a")];
        var primary = fixture.Server.Input.Admit(bodyIndex: bodyIndex, principal: occupant, sources: sources);
        var producer = new WorldInputProducer(catalog, primary.Mapping, sources, 128, primary.Revision, primary.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        var body = fixture.Server.Body(index: bodyIndex)!;

        var accepted = fixture.Server.ApplyIntentSubmission(body: body, submission: new(tick, bodyIndex, default, occupant, Input: producer.Publish(tick)));

        Assert.True(condition: accepted.IsAccepted, userMessage: accepted.ToString());
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

}
