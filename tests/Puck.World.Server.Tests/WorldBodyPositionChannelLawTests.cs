using Puck.Commands;

using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: <c>$bodyPosition:&lt;bodyRef&gt;:x|y|z</c> reads each world axis of a live body's position as the exact
/// fixed-point value its step left, a body reference resolving to no live body reads 0, and an axis other than x, y or
/// z is refused at compile by name.
/// </summary>
public sealed class WorldBodyPositionChannelLawTests {
    private const string AbsentRow = "absent";
    private const string XRow = "x";
    private const string YRow = "y";
    private const string ZRow = "z";

    private static WorldRule Mirror(string row, string channel) => new(
        Name: CellName.Parse(candidate: $"{row}-mirror"),
        Effects: [new ActionEffect.SetState(
            State: row,
            FromState: channel
        )]
    );
    private static WorldDefinition PositionDocument(string? yChannel = null) => Fixtures.BuildGradientUpDocument(gradientUp: false) with {
        StateRaw = new WorldStateSection(World: [
            StateFixtures.FixedSlot(name: XRow, rawBits: 0L),
            StateFixtures.FixedSlot(name: YRow, rawBits: 0L),
            StateFixtures.FixedSlot(name: ZRow, rawBits: 0L),
            StateFixtures.FixedSlot(name: AbsentRow, rawBits: 1L),
        ]),
        Rules = [
            Mirror(channel: $"{WorldRuleFacts.BodyPositionPrefix}body:0:x", row: XRow),
            Mirror(channel: (yChannel ?? $"{WorldRuleFacts.BodyPositionPrefix}body:0:y"), row: YRow),
            Mirror(channel: $"{WorldRuleFacts.BodyPositionPrefix}body:0:z", row: ZRow),
            Mirror(channel: $"{WorldRuleFacts.BodyPositionPrefix}body:3:y", row: AbsentRow),
        ],
    };

    [Fact]
    public void EachAxisReadsTheBodysSteppedPosition_ControlAnAbsentBodyReadsZero() {
        using var fixture = Fixtures.FreshServer(definition: PositionDocument());
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        body.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 3f,
            y: -60f,
            yawRadians: 0f,
            z: -5f
        );
        fixture.Step();

        var position = body.FixedPosition;

        Assert.Equal(
            expected: position.X.Value,
            actual: fixture.SlotValue(row: XRow)
        );
        Assert.Equal(
            expected: position.Y.Value,
            actual: fixture.SlotValue(row: YRow)
        );
        Assert.Equal(
            expected: position.Z.Value,
            actual: fixture.SlotValue(row: ZRow)
        );
        Assert.True(
            condition: (fixture.SlotValue(row: YRow) < (-50L << 16)),
            userMessage: $"y read {fixture.SlotValue(row: YRow)} raw for a body posed at y = -60"
        );
        Assert.Equal(
            expected: 0L,
            actual: fixture.SlotValue(row: AbsentRow)
        );
    }
    [Fact]
    public void ThePositionChannelRefusesAnAxisThatIsNotXYOrZ_ControlAWellFormedOneValidates() {
        var malformed = PositionDocument(yChannel: $"{WorldRuleFacts.BodyPositionPrefix}body:0:w");
        var control = PositionDocument();

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: malformed,
            reason: out var reason
        ));
        Assert.Contains(
            expectedSubstring: nameof(WorldRuleRefusal.BodyPositionMalformed),
            actualString: reason,
            comparisonType: StringComparison.Ordinal
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: control,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
}
