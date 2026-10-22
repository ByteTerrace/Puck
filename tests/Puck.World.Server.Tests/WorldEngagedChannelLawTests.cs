using Puck.Commands;

using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: <c>$engaged:&lt;bodyRef&gt;:&lt;screen&gt;</c> reads 1 while the body's participant holds a control
/// application onto the screen, captured or mirrored, and 0 once it dissolves; a body reference resolving to no live
/// body reads 0, and a screen index the document declares no screen at is refused at compile by name.
/// </summary>
public sealed class WorldEngagedChannelLawTests {
    private const string AbsentRow = "absent";
    private const string EngagedRow = "engaged";

    private static WorldRule Mirror(string row, string channel) => new(
        Name: CellName.Parse(candidate: $"{row}-mirror"),
        Effects: [new ActionEffect.SetState(
            State: row,
            FromState: channel
        )]
    );
    private static WorldDefinition EngagedDocument(int screen = Fixtures.TestPatternScreenIndex) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [
            StateFixtures.IntSlot(name: EngagedRow, value: 0L),
            StateFixtures.IntSlot(name: AbsentRow, value: 1L),
        ]),
        Rules = [
            Mirror(channel: $"{WorldRuleFacts.EngagedPrefix}body:0:{screen}", row: EngagedRow),
            Mirror(channel: $"{WorldRuleFacts.EngagedPrefix}body:3:{Fixtures.TestPatternScreenIndex}", row: AbsentRow),
        ],
    };

    [Fact]
    public void TheChannelReadsTheBodysApplicationOntoTheScreen_ControlADissolvedOrAbsentBodyReadsZero() {
        using var fixture = Fixtures.FreshServer(definition: EngagedDocument());
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: seat,
            Slot: seat.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        fixture.Step();
        Assert.Equal(expected: 0L, actual: fixture.SlotValue(row: EngagedRow));

        Assert.True(condition: fixture.Server.Engagement.Compose(
            entityIndex: seat.Index,
            target: GrantSubject.Screen(index: Fixtures.TestPatternScreenIndex),
            exclusive: false,
            actingPrincipal: seat,
            targetPrincipal: seat
        ));
        fixture.Step();
        Assert.Equal(expected: 1L, actual: fixture.SlotValue(row: EngagedRow));
        Assert.Equal(expected: 0L, actual: fixture.SlotValue(row: AbsentRow));

        Assert.Equal(
            expected: ControlOutcome.Dissolved,
            actual: fixture.Server.Engagement.Dissolve(
                entityIndex: seat.Index,
                actingPrincipal: seat,
                targetPrincipal: seat
            )
        );
        fixture.Step();
        Assert.Equal(expected: 0L, actual: fixture.SlotValue(row: EngagedRow));
    }
    [Fact]
    public void TheChannelRefusesAnUndeclaredScreen_ControlADeclaredOneValidates() {
        var malformed = EngagedDocument(screen: 31);
        var control = EngagedDocument();

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: malformed,
            reason: out var reason
        ));
        Assert.Contains(
            expectedSubstring: nameof(WorldRuleRefusal.EngagedMalformed),
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
