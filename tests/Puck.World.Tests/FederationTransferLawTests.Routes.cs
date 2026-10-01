using System.Numerics;
using Puck.Maths;
using Puck.Networking;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class FederationTransferLawTests {
    private static WorldTransferCommitMember Turned(FixedQ4816 travelTurn) => new(
        Profile: null,
        HasMappedArrival: true,
        BodyMotionProgramName: "grounded",
        Position: default,
        YawRadians: default,
        PlanarVelocity: default,
        VerticalVelocity: default,
        TravelTurn: travelTurn
    );
    private static WorldAuthorityRouteDescription Route(FixedQ4816 travelTurn) => new(
        BodyColor: Vector3.Zero,
        CatalogRig: 0,
        Definition: Fixtures.BuildDocument(),
        Endpoint: "127.0.0.1:42001",
        Entity: new WorldEntityAddress(
            Authority: "world/onward",
            Generation: 1,
            Index: 0
        ),
        Kit: 0,
        Look: 0,
        Orientation: FixedQuaternion.Identity,
        PlacementId: null,
        Position: default,
        Tick: 1UL,
        TravelTurn: travelTurn,
        Version: new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 1L)
    );

    // THE LAW: a seat follows its traveler through a door an authority elsewhere hands it on through as it follows it
    // through one this host maps. The onward authority's commit carries the traveler's accumulated arrival turn over
    // the wire, the destination keeps it on the occupant, and the route it describes carries it back; the observer
    // turns the seat's view by the turn between the one it held and the route's. The seam is turned (37 against 250
    // degrees) and the traveler arrives with an earlier half turn already accumulated, so only the change since the
    // observer last turned may move the view. The red leg is today's route, which carries no turn: the seat's camera
    // after the handoff looks away from what the seam's window showed.
    [Fact]
    public void ARouteHandedOnThroughATurnedSeamTurnsTheSeatThatFollowsIt() {
        using var fixture = Fixtures.FreshServer();
        var source = WorldWindowCrossingLawTests.Face(
            origin: new Vector3(x: 0f, y: 1.5f, z: -6f),
            yawDegrees: 37f
        );
        var destination = WorldWindowCrossingLawTests.Face(
            origin: new Vector3(x: 3f, y: 1.5f, z: -10f),
            yawDegrees: 250f
        );
        var sourceFrame = WorldWindowCrossingLawTests.Frame(face: source);
        var destinationFrame = WorldWindowCrossingLawTests.Frame(face: destination);
        var position = new Vector3(x: 0.4f, y: 0f, z: -5.8f);
        var yaw = FixedQ4816.FromDouble(value: 2.9);
        var arrival = WorldFrameIsometry.MapArrival(
            destination: in destinationFrame,
            source: in sourceFrame,
            travelerPlanarVelocity: FixedVector3.Zero,
            travelerPosition: FixedVector3.FromVector3(value: position),
            travelerVerticalVelocity: FixedQ4816.Zero,
            travelerYawRadians: yaw
        );
        var held = WorldFrameIsometry.AccumulateTurn(
            arrivalYaw: FixedQ4816.FromDouble(value: (0.4 + Math.PI)),
            departureYaw: FixedQ4816.FromDouble(value: 0.4),
            travelTurn: FixedQ4816.Zero
        );
        var request = Reservation(
            border: "onward",
            sourceAuthority: "machine-b/boot",
            transferId: 41
        );
        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );

        var encodedCommit = WorldFederationCodec.EncodeCommit(
            members: [new WorldTransferCommitMember(
                Profile: null,
                HasMappedArrival: true,
                BodyMotionProgramName: "grounded",
                Position: arrival.Position,
                YawRadians: arrival.YawRadians,
                PlanarVelocity: arrival.PlanarVelocity,
                VerticalVelocity: arrival.VerticalVelocity,
                TravelTurn: WorldFrameIsometry.AccumulateTurn(
                    arrivalYaw: arrival.YawRadians,
                    departureYaw: yaw,
                    travelTurn: held
                )
            )],
            sourceAuthority: request.SourceAuthority,
            transferId: request.TransferId
        );

        Assert.True(
            condition: WorldFederationCodec.TryDecodeCommit(
                body: encodedCommit,
                defaults: fixture.Server.Definition.PlayerDefaults,
                failure: out var commitFailure,
                members: out var members,
                sourceAuthority: out _,
                transferId: out _
            ),
            userMessage: commitFailure.ToString()
        );
        Assert.True(
            condition: fixture.Server.CommitTransfer(
                members: members,
                reason: out var commitReason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            ),
            userMessage: commitReason
        );

        var described = fixture.Server.ExecuteAuthorityOperation(operation: () => WorldLocalForwardedAuthority.DescribeRoute(
            bodyIndex: reservation.BodyIndices[0],
            endpoint: "machine-c/boot",
            server: fixture.Server
        ));

        Assert.True(
            condition: WorldFederationCodec.TryDecodeRoute(
                body: WorldFederationCodec.EncodeRoute(
                    authority: "machine-c/boot",
                    revision: 0,
                    route: in described,
                    tier: WorldDisclosureTier.Replica
                ),
                failure: out var routeFailure,
                route: out var route
            ),
            userMessage: routeFailure.ToString()
        );

        var turns = new WorldRoutedSeatTurns(seatCount: 1);

        turns.Hold(
            slot: 0,
            travelTurn: held
        );

        var turn = turns.Follow(
            slot: 0,
            travelTurn: route.TravelTurn
        );

        Assert.Equal(
            actual: turn,
            expected: WorldFrameIsometry.TurnBetween(
                after: arrival.YawRadians,
                before: yaw
            )
        );

        // The seat's camera before the handoff, and what the seam's window shows of it.
        var definition = Fixtures.BuildDocument();
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var view = new WorldSeatViewState();

        view.RecenterLook(
            targetYaw: 0.9f,
            views: definition.Views
        );

        var (eye, target) = WorldWindowCrossingLawTests.Camera(
            definition: definition,
            mirror: mirror,
            orientation: WorldWindowCrossingLawTests.Heading(yaw: yaw),
            position: position,
            view: view
        );
        var windowDirection = WorldWindowProjectionMath.MapVector(
            destination: destination,
            source: source,
            vector: Vector3.Normalize(value: (target - eye))
        );

        Vector3 ArrivedDirection() {
            var (arrivedEye, arrivedTarget) = WorldWindowCrossingLawTests.Camera(
                definition: definition,
                mirror: mirror,
                orientation: route.Orientation.ToQuaternion(),
                position: route.Position.ToVector3(),
                view: view
            );

            return Vector3.Normalize(value: (arrivedTarget - arrivedEye));
        }

        Assert.True(
            condition: (Vector3.Distance(value1: ArrivedDirection(), value2: windowDirection) > 0.1f),
            userMessage: "a route that carries no turn leaves the seat looking along the seam's window"
        );

        view.Cross(
            turn: WorldSeatViewState.ArrivalTurn(yawDelta: turn),
            yawReference: definition.Views.SeatControl.YawReference
        );
        WorldWindowCrossingLawTests.Near(
            actual: ArrivedDirection(),
            expected: windowDirection,
            what: "camera direction after the handoff"
        );
    }
    // THE LAW: an accumulated arrival turn is reduced to [-pi, pi), so a reader refuses any other value by name: the
    // federation commit and route leaves, and the destination's escrow for a colocated commit. -pi is the interval's
    // closed end and is accepted; pi is its open end and is refused.
    [Fact]
    public void AnUnreducedArrivalTurnIsRefusedByEveryReader() {
        using var fixture = Fixtures.FreshServer();
        var pi = FixedQ4816.FromDouble(value: Math.PI);

        foreach (var turn in new[] { pi, FixedQ4816.FromInteger(value: 4), -FixedQ4816.FromInteger(value: 4) }) {
            Assert.False(condition: WorldFederationCodec.TryDecodeCommit(
                body: WorldFederationCodec.EncodeCommit(
                    members: [Turned(travelTurn: turn)],
                    sourceAuthority: "machine-b/boot",
                    transferId: 51
                ),
                defaults: fixture.Server.Definition.PlayerDefaults,
                failure: out var commitFailure,
                members: out _,
                sourceAuthority: out _,
                transferId: out _
            ));
            Assert.Equal(
                actual: commitFailure.Refusal,
                expected: WireRefusal.PayloadMalformed
            );
            Assert.Contains(
                actualString: commitFailure.Detail,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "travel turn"
            );

            var route = Route(travelTurn: turn);

            Assert.False(condition: WorldFederationCodec.TryDecodeRoute(
                body: WorldFederationCodec.EncodeRoute(
                    authority: "world/onward",
                    revision: 0,
                    route: in route,
                    tier: WorldDisclosureTier.Replica
                ),
                failure: out var routeFailure,
                route: out _
            ));
            Assert.Equal(
                actual: routeFailure.Refusal,
                expected: WireRefusal.PayloadMalformed
            );
            Assert.Contains(
                actualString: routeFailure.Detail,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "route travel turn"
            );
        }

        var request = Reservation(
            border: "onward",
            sourceAuthority: "machine-b/boot",
            transferId: 52
        );

        Assert.True(condition: fixture.Server.ReserveTransfer(request: request).Accepted);
        Assert.False(condition: fixture.Server.CommitTransfer(
            members: [Turned(travelTurn: pi)],
            reason: out var reason,
            sourceAuthority: request.SourceAuthority,
            transferId: request.TransferId
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "unreduced arrival turn"
        );

        var closed = Route(travelTurn: -pi);

        Assert.True(condition: WorldFederationCodec.TryDecodeRoute(
            body: WorldFederationCodec.EncodeRoute(
                authority: "world/onward",
                revision: 0,
                route: in closed,
                tier: WorldDisclosureTier.Replica
            ),
            failure: out var closedFailure,
            route: out var decoded
        ), userMessage: closedFailure.ToString());
        Assert.Equal(
            actual: decoded.TravelTurn,
            expected: -pi
        );
    }
    [Fact]
    public void RouteWire_PreservesOneCompleteAuthorityEpoch() {
        var expected = new WorldAuthorityRouteDescription(
            Endpoint: "127.0.0.1:42001",
            Entity: new WorldEntityAddress(
                Authority: "world/corner-sw",
                Generation: 23,
                Index: 17
            ),
            Tick: 987654321UL,
            Position: new FixedVector3(
                X: FixedQ4816.FromDouble(value: -12.25),
                Y: FixedQ4816.FromDouble(value: 3.5),
                Z: FixedQ4816.FromDouble(value: 0.125)
            ),
            Orientation: FixedQuaternion.FromAxisAngle(
                axis: new FixedVector3(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.Zero
                ),
                angle: FixedQ4816.FromDouble(value: 1.75)
            ),
            BodyColor: new Vector3(x: 0.25f, y: 0.5f, z: 0.75f),
            Kit: 0,
            Look: 0,
            CatalogRig: 71,
            TravelTurn: FixedQ4816.FromDouble(value: -2.25),
            PlacementId: "traveler-shell",
            Definition: Fixtures.BuildDocument(),
            Version: new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 42L)
        );

        var encoded = WorldFederationCodec.EncodeRoute(
            authority: "world/corner-sw",
            revision: 0,
            route: in expected,
            tier: WorldDisclosureTier.Replica
        );

        Assert.True(
            condition: WorldFederationCodec.TryDecodeRoute(
                body: encoded,
                failure: out var failure,
                route: out var actual
            ),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            expected: expected.Endpoint,
            actual: actual.Endpoint
        );
        Assert.Equal(
            expected: expected.Entity,
            actual: actual.Entity
        );
        Assert.Equal(
            expected: expected.Tick,
            actual: actual.Tick
        );
        Assert.Equal(actual: actual.Version, expected: expected.Version);
        Assert.Equal(
            expected: expected.Position,
            actual: actual.Position
        );
        Assert.Equal(
            expected: expected.Orientation,
            actual: actual.Orientation
        );
        Assert.Equal(
            expected: expected.BodyColor,
            actual: actual.BodyColor
        );
        Assert.Equal(
            expected: expected.Kit,
            actual: actual.Kit
        );
        Assert.Equal(
            expected: expected.Look,
            actual: actual.Look
        );
        Assert.Equal(
            expected: expected.CatalogRig,
            actual: actual.CatalogRig
        );
        Assert.Equal(
            expected: expected.TravelTurn,
            actual: actual.TravelTurn
        );
        Assert.Equal(
            expected: expected.PlacementId,
            actual: actual.PlacementId
        );
        Assert.Equal(
            expected: expected.Definition.DocumentId,
            actual: actual.Definition.DocumentId
        );
    }
}
