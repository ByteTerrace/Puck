using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>What a portal's window shows is what its traveler sees after walking through. The window maps the seat's
/// camera through the door's isometry (<see cref="WorldWindowProjectionMath"/>); a crossing maps the body through the
/// same isometry (<see cref="WorldFrameIsometry.MapArrival"/>) and carries the seat's view with it
/// (<see cref="WorldSeatViewState.Cross"/>). Both must agree for any authored pair, including one that turns the
/// traveler.</summary>
public sealed class WorldWindowCrossingLawTests {
    internal const float Tolerance = 1e-3f;

    // The (Right, Up, Normal) triad WorldFaceCatalog derives for an unrotated face at an authored yaw.
    internal static WorldFaceGeometry Face(Vector3 origin, float yawDegrees) {
        var yaw = (yawDegrees * (MathF.PI / 180f));

        return new WorldFaceGeometry(
            Origin: origin,
            Right: new Vector3(
                x: MathF.Cos(x: yaw),
                y: 0f,
                z: -MathF.Sin(x: yaw)
            ),
            Up: Vector3.UnitY,
            Normal: new Vector3(
                x: MathF.Sin(x: yaw),
                y: 0f,
                z: MathF.Cos(x: yaw)
            ),
            HalfWidth: 1.5f,
            HalfHeight: 1.5f
        );
    }
    internal static WorldFaceFrame Frame(WorldFaceGeometry face) => new(
        Origin: FixedVector3.FromVector3(value: face.Origin),
        Right: FixedVector3.FromVector3(value: face.Right),
        Up: FixedVector3.FromVector3(value: face.Up),
        Normal: FixedVector3.FromVector3(value: face.Normal),
        HalfWidth: FixedQ4816.FromDouble(value: face.HalfWidth),
        HalfHeight: FixedQ4816.FromDouble(value: face.HalfHeight),
        HalfDepth: FixedQ4816.Zero
    );
    internal static Quaternion Heading(FixedQ4816 yaw) => Quaternion.CreateFromAxisAngle(
        angle: ((float)((double)yaw)),
        axis: Vector3.UnitY
    );
    internal static void Near(Vector3 expected, Vector3 actual, string what) => Assert.True(
        condition: (Vector3.Distance(value1: expected, value2: actual) <= Tolerance),
        userMessage: $"{what}: expected {expected}, actual {actual}"
    );
    // The seat camera the presenter frames a body with: the seat's compiled chase rig, posed at the body.
    internal static (Vector3 Eye, Vector3 Target) Camera(WorldSeatViewState view, WorldDefinition definition, WorldStateMirror mirror, Vector3 position, Quaternion orientation) {
        var rig = view.ResolveChase(
            bodyOrientation: orientation,
            definition: definition,
            mirror: mirror,
            views: definition.Views
        );
        var anchor = new SdfAnchor(
            Orientation: orientation,
            Position: position
        );
        var clock = new SdfCameraClock(
            AuthoritativeTick: 0UL,
            PresentationSeconds: 0f
        );

        var (eye, target, _) = rig.Resolve(
            anchor: in anchor,
            clock: in clock
        );

        return (eye, target);
    }

    // THE LAW: a seat that walks through a door looks along what the door's window showed it. The window maps the
    // seat camera's eye and view direction through the pair's isometry; the crossing maps the body through it, and
    // the seat's view follows. A pair 180 degrees apart (the identity) agrees without any turn; a pair authored at one
    // yaw turns the traveler by a half turn, and 37/250 by an arbitrary angle whose sign a mirrored turn would get
    // wrong. The red leg is a crossing that does not carry the view: a world-referenced look keeps its old heading.
    [InlineData(180f, 0f, WorldSeatYawReference.World)]
    [InlineData(180f, 180f, WorldSeatYawReference.World)]
    [InlineData(37f, 250f, WorldSeatYawReference.World)]
    [InlineData(180f, 180f, WorldSeatYawReference.Body)]
    [InlineData(37f, 250f, WorldSeatYawReference.Body)]
    [Theory]
    public void ASeatLooksAfterACrossingAlongWhatTheDoorsWindowShowed(float sourceYaw, float destinationYaw, WorldSeatYawReference yawReference) {
        var source = Face(
            origin: new Vector3(x: 0f, y: 1.5f, z: -6f),
            yawDegrees: sourceYaw
        );
        var destination = Face(
            origin: new Vector3(x: 3f, y: 1.5f, z: -10f),
            yawDegrees: destinationYaw
        );
        var sourceFrame = Frame(face: source);
        var destinationFrame = Frame(face: destination);
        var document = Fixtures.BuildDocument();
        var definition = (document with {
            ViewsRaw = document.Views with {
                SeatControlRaw = document.Views.SeatControl with { YawReference = yawReference },
            },
        });
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var position = new Vector3(x: 0.4f, y: 0f, z: -5.8f);
        var yaw = FixedQ4816.FromDouble(value: 2.9);
        var view = new WorldSeatViewState();

        view.RecenterLook(
            targetYaw: 0.9f,
            views: definition.Views
        );

        var (eye, target) = Camera(
            definition: definition,
            mirror: mirror,
            orientation: Heading(yaw: yaw),
            position: position,
            view: view
        );
        var dynamics = new SdfCameraDynamics(
            Damping: 1f,
            Frequency: 2f,
            Response: 0f
        );
        var seeded = eye;

        view.Follow(
            deltaSeconds: (1f / 60f),
            dynamics: in dynamics,
            enabled: true,
            eye: ref seeded,
            target: ref target
        );

        // What the window shows: the seat camera mapped through the door.
        var windowEye = WorldWindowProjectionMath.MapPoint(
            destination: destination,
            point: eye,
            source: source
        );
        var windowDirection = WorldWindowProjectionMath.MapVector(
            destination: destination,
            source: source,
            vector: Vector3.Normalize(value: (target - eye))
        );

        // The crossing: the body mapped through the door, and the seat's view carried with it.
        var arrival = WorldFrameIsometry.MapArrival(
            destination: in destinationFrame,
            source: in sourceFrame,
            travelerPlanarVelocity: FixedVector3.Zero,
            travelerPosition: FixedVector3.FromVector3(value: position),
            travelerVerticalVelocity: FixedQ4816.Zero,
            travelerYawRadians: yaw
        );

        view.Cross(
            turn: WorldSeatViewState.ArrivalTurn(yawDelta: (arrival.YawRadians - yaw)),
            yawReference: yawReference
        );

        // The body faces along the window's mapping of its own facing.
        Near(
            actual: Vector3.Transform(
                rotation: Heading(yaw: arrival.YawRadians),
                value: Vector3.UnitZ
            ),
            expected: WorldWindowProjectionMath.MapVector(
                destination: destination,
                source: source,
                vector: Vector3.Transform(
                    rotation: Heading(yaw: yaw),
                    value: Vector3.UnitZ
                )
            ),
            what: "body facing"
        );

        var (arrivedEye, arrivedTarget) = Camera(
            definition: definition,
            mirror: mirror,
            orientation: Heading(yaw: arrival.YawRadians),
            position: arrival.Position.ToVector3(),
            view: view
        );

        Near(
            actual: Vector3.Normalize(value: (arrivedTarget - arrivedEye)),
            expected: windowDirection,
            what: "camera direction"
        );
        Near(
            actual: arrivedEye,
            expected: windowEye,
            what: "camera eye"
        );

        // The eased boom turned with the view, so the first eased frame after the crossing holds the framing rather
        // than swinging round from the heading the seat left with.
        var eased = arrivedEye;

        view.Follow(
            deltaSeconds: (1f / 60f),
            dynamics: in dynamics,
            enabled: true,
            eye: ref eased,
            target: ref arrivedTarget
        );
        Near(
            actual: eased,
            expected: arrivedEye,
            what: "eased eye"
        );
    }
}
