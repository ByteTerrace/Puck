using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a seat captured at a tabletop publishes that table, from the same application-set read as every other
/// engagement fact: the perception anchor names the placement the board view frames, and the <c>engagement</c>
/// context family reads <c>tabletop</c>, so a world's context row can give the seat the table's controls. A mirrored
/// set still walks its avatar and sits at nothing. A seat captured on a screen showing a window capture reads
/// <c>passthrough</c>, so a world can give it a pad's desktop controls there; any other captured screen reads
/// <c>engaged</c>.
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

        void Engage(GrantSubject target, bool capture, IReadOnlyList<WorldScreen> screens) {
            // Back to the own-body default first (a no-op when already there), so each case holds one target.
            _ = fixture.Server.Engagement.Dissolve(
                actingPrincipal: seat,
                entityIndex: 0,
                targetPrincipal: seat
            );
            Assert.True(condition: fixture.Server.Engagement.Compose(
                actingPrincipal: seat,
                entityIndex: 0,
                exclusive: capture,
                target: target,
                targetPrincipal: seat
            ));
            WorldSeatContextSync.Publish(
                activeLayout: WorldContextFamilies.LayoutBuiltin,
                anchor: anchor,
                grants: fixture.Server.Grants,
                roster: roster,
                screens: screens,
                seatBindings: bindings
            );
        }

        Engage(capture: true, screens: [], target: GrantSubject.Placement(id: "table"));
        Assert.Equal(expected: "table", actual: anchor.EngagedTabletop(slot: 0));
        Assert.Equal(expected: WorldContextFamilies.EngagementTabletop, actual: bindings.ModeState(family: WorldContextFamilies.Engagement, slot: 0));
        Assert.Equal(expected: -1, actual: anchor.EngagedScreen(slot: 0));

        Engage(capture: false, screens: [], target: GrantSubject.Placement(id: "table"));
        Assert.Null(@object: anchor.EngagedTabletop(slot: 0));
        Assert.Equal(expected: WorldContextFamilies.EngagementEngaged, actual: bindings.ModeState(family: WorldContextFamilies.Engagement, slot: 0));
        Assert.Null(@object: anchor.EngagedTabletop(slot: WorldSeatBindings.SeatCount));

        // Captured on screen 16: a window capture's screen and a monitor capture's read passthrough; a screen the
        // document does not declare reads engaged. Mirrored, it frames nothing and reads engaged.
        Engage(capture: true, screens: [Screen(index: 16, monitor: null)], target: GrantSubject.Screen(index: 16));
        Assert.Equal(expected: 16, actual: anchor.EngagedScreen(slot: 0));
        Assert.Equal(expected: WorldContextFamilies.EngagementPassthrough, actual: bindings.ModeState(family: WorldContextFamilies.Engagement, slot: 0));
        Engage(capture: true, screens: [Screen(index: 2, monitor: null), Screen(index: 16, monitor: 0)], target: GrantSubject.Screen(index: 16));
        Assert.Equal(expected: WorldContextFamilies.EngagementPassthrough, actual: bindings.ModeState(family: WorldContextFamilies.Engagement, slot: 0));
        Engage(capture: true, screens: [Screen(index: 2, monitor: null)], target: GrantSubject.Screen(index: 16));
        Assert.Equal(expected: WorldContextFamilies.EngagementEngaged, actual: bindings.ModeState(family: WorldContextFamilies.Engagement, slot: 0));
        Engage(capture: false, screens: [Screen(index: 16, monitor: null)], target: GrantSubject.Screen(index: 16));
        Assert.Equal(expected: -1, actual: anchor.EngagedScreen(slot: 0));
        Assert.Equal(expected: WorldContextFamilies.EngagementEngaged, actual: bindings.ModeState(family: WorldContextFamilies.Engagement, slot: 0));
    }

    // A screen showing a desktop capture: a window's, or the whole monitor's when one is named.
    private static WorldScreen Screen(int index, int? monitor) => new(
        HalfDepth: 0.01f,
        HalfHeight: 0.2f,
        HalfWidth: 0.3f,
        Index: index,
        Origin: new Puck.Assets.Documents.DocumentVector3(value: Vector3.UnitY),
        Right: new Puck.Assets.Documents.DocumentVector3(value: Vector3.UnitX),
        Round: 0f,
        Route: new WorldScreenRoute(
            EngageRadius: 3f,
            Engageable: true
        ),
        Source: WorldImageProducerSettings.SourceOf(
            id: WorldImageProducerSettings.CaptureId,
            settings: new WorldCaptureSettings(
                MonitorIndex: monitor,
                Profile: new WorldFeedProfile(Height: 240, RefreshRateHz: 30, Width: 320),
                WindowTitle: ((monitor is null) ? "Claude" : null)
            )
        ),
        Up: new Puck.Assets.Documents.DocumentVector3(value: Vector3.UnitY)
    );
}
