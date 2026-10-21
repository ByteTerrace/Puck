using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a seat captured at a tabletop publishes that table, from the same application-set read as every other
/// engagement fact: the perception anchor names the placement the board view frames, and the <c>engagement</c>
/// context family reads <c>tabletop</c>, so a world's context row can give the seat the table's controls. A mirrored
/// set still walks its avatar and sits at nothing.
/// </summary>
public sealed class WorldSeatContextSyncTabletopLawTests {
    [Fact]
    public void ACapturedSeatAtATablePublishesTheTableAndTheTabletopEngagementState() {
        using var fixture = Fixtures.FreshServer();
        var definition = fixture.Server.Definition;
        var seat = Principal.Seat(slot: 0);
        var bindings = new WorldSeatBindings(definition: definition);
        var roster = new PlayerRoster(
            definition: definition,
            link: new LoopbackTransport(server: fixture.Server),
            seatBindings: bindings
        );
        var anchor = new WorldPerceptionAnchor();

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: seat,
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));

        void Sit(bool capture) {
            Assert.True(condition: fixture.Server.Engagement.Compose(
                actingPrincipal: seat,
                entityIndex: 0,
                exclusive: capture,
                target: GrantSubject.Placement(id: "table"),
                targetPrincipal: seat
            ));
            WorldSeatContextSync.Publish(
                activeLayout: WorldContextFamilies.LayoutBuiltin,
                anchor: anchor,
                grants: fixture.Server.Grants,
                roster: roster,
                seatBindings: bindings
            );
        }

        Sit(capture: true);
        Assert.Equal(expected: "table", actual: anchor.EngagedTabletop(slot: 0));
        Assert.Equal(expected: WorldContextFamilies.EngagementTabletop, actual: bindings.ModeState(family: WorldContextFamilies.Engagement, slot: 0));
        Assert.Equal(expected: -1, actual: anchor.EngagedScreen(slot: 0));

        Sit(capture: false);
        Assert.Null(@object: anchor.EngagedTabletop(slot: 0));
        Assert.Equal(expected: WorldContextFamilies.EngagementEngaged, actual: bindings.ModeState(family: WorldContextFamilies.Engagement, slot: 0));
        Assert.Null(@object: anchor.EngagedTabletop(slot: WorldSeatBindings.SeatCount));
    }
}
