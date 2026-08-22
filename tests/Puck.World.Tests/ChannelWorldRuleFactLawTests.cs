using Xunit;

using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>$channel:&lt;seat&gt;:&lt;channelName&gt;</c> reads a declared <c>channels[]</c> row's live per-tick
/// value for one 1-based local seat, in the channel's own native fixed-point domain (no rescale), and refuses a seat
/// outside <c>population.localSeats</c> or a channel name the document does not declare.
/// </summary>
public sealed class ChannelWorldRuleFactLawTests {
    private const string BeaconRow = "beacon";
    private const string PortalChannel = "portal";
    private const int PortalOrdinal = 3;

    [Fact]
    public void TheChannelFactRefusesABadSeatOrAnUndeclaredChannelName_ControlAWellFormedGateClean() {
        var badSeat = ChannelGatedDocument(gate: $"{WorldRuleFacts.ChannelPrefix}5:{PortalChannel}");
        var badChannel = ChannelGatedDocument(gate: $"{WorldRuleFacts.ChannelPrefix}1:no-such-channel");
        var control = ChannelGatedDocument(gate: $"{WorldRuleFacts.ChannelPrefix}1:{PortalChannel}");

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: badSeat, reason: out var seatReason));
        Assert.Contains(expectedSubstring: nameof(WorldRuleRefusal.ChannelMalformed), actualString: seatReason, comparisonType: StringComparison.Ordinal);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: badChannel, reason: out var channelReason));
        Assert.Contains(expectedSubstring: nameof(WorldRuleRefusal.ChannelMalformed), actualString: channelReason, comparisonType: StringComparison.Ordinal);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void ARuleGatedOnAChannelFactFiresOnlyAfterThatSeatsChannelReachesTheThreshold() {
        using var fixture = Fixtures.FreshServer(definition: ChannelGatedDocument(gate: $"{WorldRuleFacts.ChannelPrefix}1:{PortalChannel}"));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        // Control half: an ordinary tick with no channel submission never crosses the gate.
        fixture.Step();
        Assert.Equal(expected: 0L, actual: BeaconCell(fixture: fixture));

        // The discriminating write: the seat's own drained submission sets the portal channel to fully-pressed.
        fixture.Server.ApplyIntentSubmission(
            body: body,
            submission: new IntentSubmission(
                Tick: 0UL,
                EntityIndex: actor.Index,
                Intent: default(PlayerIntent).WithChannel(ordinal: PortalOrdinal, value: FixedQ4816.One),
                Principal: actor
            )
        );
        fixture.Step();

        Assert.Equal(expected: 1L, actual: BeaconCell(fixture: fixture));
    }

    private static long BeaconCell(WorldFixture fixture) =>
        fixture.Server.Definition.State.Single(predicate: static row => (row.Name.Value == BeaconRow)).Cells!.Single().Value;
    // A document carrying the base fixture's three role channels plus one composition channel ("portal", ordinal 3)
    // and a rule that writes the "beacon" row once the authored gate holds — the discriminating write under test is
    // always which STRING names the gate.
    private static WorldDefinition ChannelGatedDocument(string gate) {
        var document = Fixtures.BuildDocument();
        var beacon = WorldCellName.Parse(candidate: BeaconRow);

        return document with {
            ChannelsRaw = [.. document.Channels, new WorldChannel(Name: PortalChannel, Shape: ChannelShape.Unipolar, Composition: true)],
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: beacon,
                    Kind: CellKind.Int,
                    NonNegative: true,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)])
            ]),
            Rules = [
                new WorldRule(
                    Name: WorldCellName.Parse(candidate: "channel-beacon"),
                    Gate: new ActionPredicate.CompareState(
                        State: gate,
                        Comparison: ActionStateComparison.GreaterOrEqual,
                        Value: 1f
                    ),
                    Effects: [new ActionEffect.SetState(State: beacon.Value, Value: 1f)])
            ],
        };
    }
}
