using Puck.Commands;
using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for a local seat that follows its traveler across hosts: a source host transfers the traveler over
/// real federation to a destination behind its door, and the destination hands it on to a third authority.</summary>
public sealed class FederationOnwardRouteLawTests {
    // THE LAW: a local seat follows its traveler through three authorities and takes the onward turn exactly once,
    // wherever the onward route's observation falls against the seat's claim. The source host commits the traveler to
    // the destination behind its door, starts a route wrapper for the followed seat, holds the commit's turn and
    // publishes the claim through the wrapper's route gate. The destination hands the traveler on to its colocated
    // onward row, whose occupant has turned since, and the wrapper observes the onward route: before the claim (the
    // claim's delivery takes it), during the claim's delivery of the destination's route (the observation waits on the
    // gate and lands after it), or after the claim (it is delivered as it comes). In each case the seat ends routed to
    // the onward entity through the wrapper's endpoint, its view turned once by exactly the turn between the commit's and
    // the onward route's. Red legs: without the claim's delivery, the route observed before the claim is never taken;
    // without the gate, the destination's older route is delivered after the onward one, retargets the seat back to the
    // destination's entity, and turns the view back.
    [InlineData("before")]
    [InlineData("during")]
    [InlineData("after")]
    [Theory]
    public void ASeatFollowsItsTravelerThroughThreeAuthoritiesAndTakesTheOnwardTurnExactlyOnce(string interleaving) {
        var committed = FixedQ4816.FromDouble(value: 2.5);
        var onward = WorldFrameIsometry.AccumulateTurn(
            arrivalYaw: FixedQ4816.FromInteger(value: 2),
            departureYaw: FixedQ4816.Zero,
            travelTurn: committed
        );
        FederatedHosts? hosts = null;
        WorldRemoteAuthority? traveler = null;
        WorldAuthorityRouteDescription? onwardRoute = null;
        Thread? concurrent = null;
        bool? observedBeforeDeliveryEnded = null;
        var started = 0;

        using var built = FederatedHosts.Build(travelerRouteStarted: wrapper => {
            started++;
            traveler = wrapper;
            if (interleaving == "after") {
                return;
            }

            var route = hosts!.HandOn(
                travelTurn: onward,
                traveler: wrapper
            );

            onwardRoute = route;
            if (interleaving == "before") {
                wrapper.ObserveRoute(route: route);
                return;
            }

            // The claim's delivery of the destination's route retargets the seat; the onward route is observed on another
            // thread while that retarget is underway, and the gate holds the observation back until the delivery ends.
            hosts.Seats.Retargeting = _ => {
                if (concurrent is not null) {
                    return;
                }

                concurrent = new Thread(start: () => wrapper.ObserveRoute(route: route));
                concurrent.Start();
                observedBeforeDeliveryEnded = concurrent.Join(timeout: TimeSpan.FromMilliseconds(value: 500));
            };
        });

        hosts = built;
        hosts.JoinSeat(
            slot: 0,
            travelTurn: committed
        );
        _ = hosts.Source.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: hosts.RemoteDestination,
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: WorldInstanceHost.BootInstanceName,
            sourceSlot: 0
        );
        hosts.Source.DrainPendingTransfers();
        concurrent?.Join();

        Assert.Equal(
            actual: started,
            expected: 1
        );
        Assert.NotNull(@object: traveler);
        if (interleaving == "after") {
            onwardRoute = hosts.HandOn(
                travelTurn: onward,
                traveler: traveler
            );
            traveler.ObserveRoute(route: onwardRoute.Value);
        }

        var described = Assert.NotNull(value: onwardRoute);

        Assert.Equal(
            actual: described.Entity.Authority,
            expected: hosts.OnwardRow.Server.AuthorityIdentity
        );
        Assert.Equal(
            actual: described.TravelTurn,
            expected: onward
        );

        var crossings = hosts.Seats.Crossings;
        var total = crossings.Aggregate(
            func: static (sum, crossing) => WorldFrameIsometry.TurnBetween(
                after: (sum + crossing.YawDelta),
                before: FixedQ4816.Zero
            ),
            seed: FixedQ4816.Zero
        );

        Assert.Equal(
            actual: total,
            expected: WorldFrameIsometry.TurnBetween(
                after: onward,
                before: committed
            )
        );
        Assert.Equal(
            actual: crossings.Count(predicate: static crossing => (crossing.YawDelta != FixedQ4816.Zero)),
            expected: 1
        );

        var claim = hosts.Seats.Router.Route(slot: 0);

        Assert.Equal(
            actual: claim.Entity,
            expected: described.Entity
        );
        // The claim names the seat's own traveler route, whose endpoint now mirrors the onward authority.
        Assert.Equal(
            actual: claim.Endpoint.Identity,
            expected: $"$traveler/{hosts.SourceMachineId:N}/0"
        );
        Assert.Equal(
            actual: claim.Endpoint.Authority,
            expected: described.Entity.Authority
        );

        // A repeated observation of the onward route is delivered again and never turns the seat again.
        var deliveries = hosts.Seats.Deliveries;

        traveler.ObserveRoute(route: described);
        Assert.Equal(
            actual: hosts.Seats.Deliveries,
            expected: (deliveries + 1)
        );
        Assert.Equal(
            actual: hosts.Seats.Crossings.Count,
            expected: crossings.Count
        );
        if (interleaving == "during") {
            Assert.False(
                condition: observedBeforeDeliveryEnded!.Value,
                userMessage: "the onward route's observation finished while the claim's delivery held the route gate"
            );
        }
    }
}
