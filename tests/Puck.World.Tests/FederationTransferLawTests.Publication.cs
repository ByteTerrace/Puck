using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class FederationTransferLawTests {
    // THE LAW: a seat that starts following a traveler's route takes every onward turn exactly once, wherever the onward
    // route's observation falls against the seat's claim. The host holds the commit's accumulated turn, then publishes the
    // claim through WorldRemoteAuthority.PublishClaim, which runs it under the route gate and then delivers the latest
    // observed route. A route observed before the claim reached no seat and is delivered after it; a newer route observed
    // while that delivery runs waits on the gate and arrives after it; a route observed after the claim is delivered as
    // it comes. In each case the seat ends holding the latest route's turn, having turned by exactly the difference. Red
    // legs: without the delivery, the route observed before the claim is never taken; without the gate, the newer route
    // observed during the delivery is taken first and the older one then turns the seat back.
    [InlineData("before")]
    [InlineData("during")]
    [InlineData("after")]
    [Theory]
    public void AClaimPublishedUnderTheRouteGateTakesEveryOnwardTurnExactlyOnce(string interleaving) {
        var committed = FixedQ4816.FromDouble(value: 2.5);
        var onward = WorldFrameIsometry.AccumulateTurn(
            arrivalYaw: FixedQ4816.FromInteger(value: 2),
            departureYaw: FixedQ4816.Zero,
            travelTurn: committed
        );
        var newer = WorldFrameIsometry.AccumulateTurn(
            arrivalYaw: FixedQ4816.FromDouble(value: -1.25),
            departureYaw: FixedQ4816.Zero,
            travelTurn: onward
        );
        var latest = ((interleaving == "during") ? newer : onward);
        var turns = new WorldRoutedSeatTurns(seatCount: 1);
        var published = false;
        var applied = new List<FixedQ4816>();
        WorldRemoteAuthority? observed = null;
        Thread? concurrent = null;

        // Outbound-closed: the law drives the observed routes by hand, so no lane dials.
        using var network = new WorldPeerNetwork(
            allowOutbound: false,
            timeProvider: TimeProvider.System,
            transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout
        );
        using var authority = new WorldRemoteAuthority(
            endpoint: "127.0.0.1:42001",
            initialRoute: Route(travelTurn: committed),
            network: network,
            observerAuthority: "machine-a/boot",
            placeholder: Fixtures.BuildDocument(),
            routeChanged: route => {
                if (!published) {
                    return;
                }

                // The delivery of the onward route, taken while a newer route's observation is underway on another
                // thread: the gate holds that observation back until this delivery has been taken.
                if (
                    (interleaving == "during") &&
                    (concurrent is null) &&
                    (route.TravelTurn == onward)
                ) {
                    concurrent = new Thread(start: () => observed!.ObserveRoute(route: Route(travelTurn: newer)));
                    concurrent.Start();
                    _ = concurrent.Join(timeout: TimeSpan.FromMilliseconds(value: 500));
                }

                applied.Add(item: turns.Follow(
                    slot: 0,
                    travelTurn: route.TravelTurn
                ));
            },
            security: new InertAuthenticator()
        );

        observed = authority;

        if (interleaving != "after") {
            authority.ObserveRoute(route: Route(travelTurn: onward));
        }

        turns.Hold(
            slot: 0,
            travelTurn: committed
        );
        authority.PublishClaim(publish: () => published = true);
        concurrent?.Join();

        if (interleaving == "after") {
            authority.ObserveRoute(route: Route(travelTurn: onward));
        }

        var total = applied.Aggregate(
            func: static (sum, turn) => WorldFrameIsometry.TurnBetween(
                after: (sum + turn),
                before: FixedQ4816.Zero
            ),
            seed: FixedQ4816.Zero
        );

        Assert.Equal(
            actual: total,
            expected: WorldFrameIsometry.TurnBetween(
                after: latest,
                before: committed
            )
        );
        Assert.Equal(
            actual: applied.Count(predicate: static turn => (turn != FixedQ4816.Zero)),
            expected: ((interleaving == "during") ? 2 : 1)
        );

        // A repeated observation of the latest route never turns the seat again.
        var taken = applied.Count;

        authority.ObserveRoute(route: Route(travelTurn: latest));
        Assert.Equal(
            actual: applied.Skip(count: taken),
            expected: [FixedQ4816.Zero]
        );
    }
}
