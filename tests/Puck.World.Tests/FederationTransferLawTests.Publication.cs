using Puck.Maths;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class FederationTransferLawTests {
    [Fact]
    public void ARouteObservedBeforeTheSeatIsPublishedKeepsEveryOnwardTurnExactlyOnce() {
        var prior = FixedQ4816.FromDouble(value: 2.5);
        var first = FixedQ4816.One;
        var onward = FixedQ4816.FromInteger(value: 2);
        var returning = FixedQ4816.FromDouble(value: -1.25);
        var committed = WorldFrameIsometry.AccumulateTurn(
            travelTurn: prior,
            departureYaw: FixedQ4816.Zero,
            arrivalYaw: first
        );
        var final = WorldFrameIsometry.AccumulateTurn(
            travelTurn: WorldFrameIsometry.AccumulateTurn(
                travelTurn: committed,
                departureYaw: FixedQ4816.Zero,
                arrivalYaw: onward
            ),
            departureYaw: FixedQ4816.Zero,
            arrivalYaw: returning
        );
        var route = Route(travelTurn: final);
        var turns = new WorldRoutedSeatTurns(seatCount: 1);
        var view = new WorldSeatViewState();
        var published = false;
        var applied = new List<FixedQ4816>();

        view.RecenterLook(targetYaw: 0.3f, views: route.Definition.Views);
        using var authority = new WorldRemoteAuthority(
            endpoint: "127.0.0.1:42001",
            placeholder: route.Definition,
            security: new InertAuthenticator(),
            observerAuthority: "machine-a/boot",
            initialRoute: route,
            routeChanged: observed => {
                if (!published) { return; }
                var turn = turns.Follow(slot: 0, travelTurn: observed.TravelTurn);

                applied.Add(item: turn);
                view.Cross(
                    turn: WorldSeatViewState.ArrivalTurn(yawDelta: turn),
                    yawReference: WorldSeatYawReference.World
                );
            }
        );

        // Observation can deliver its one route frame before the source publishes the local seat's claim.
        authority.RepublishObservedRoute();
        Assert.Empty(collection: applied);
        turns.Hold(slot: 0, travelTurn: committed);
        published = true;
        view.Cross(
            turn: WorldSeatViewState.ArrivalTurn(yawDelta: first),
            yawReference: WorldSeatYawReference.World
        );

        // Keeping only the acknowledged first hop leaves this world-referenced view 0.75 radians behind.
        Assert.True(condition: (Math.Abs(value: (view.Yaw - 2.05f)) > 0.7f));

        authority.RepublishObservedRoute();
        Assert.Equal(expected: (onward + returning), actual: Assert.Single(collection: applied));
        Assert.InRange(actual: view.Yaw, low: 2.049f, high: 2.051f);

        // Publication retries and a repeated observation of the same arrival turn never rotate it again.
        authority.RepublishObservedRoute();
        Assert.Equal(expected: FixedQ4816.Zero, actual: applied[1]);
        Assert.InRange(actual: view.Yaw, low: 2.049f, high: 2.051f);
    }
}
