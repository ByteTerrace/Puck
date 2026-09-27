using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a seat routed to an authority the boot presentation cannot map it into (another document that no
/// adjacency relates to the boot world) is presented in that authority's world, never at foreign coordinates in the boot
/// frame. Two seats are split across two worlds: seat 1 stays home and seat 2 is routed to a destination that runs its
/// own document. <see cref="WorldContinuum.PresentedElsewhere"/> names the destination for seat 2 alone. The boot frame
/// holds no pose for seat 2, while its presented pose is the destination's own. The seat's view, drawn by the
/// destination's <see cref="WorldRoutedScene"/>, emits exactly the program the destination's delivered definition
/// composes through the session emitter, not the boot world's, and frames it with the seat's own camera.
/// </summary>
public sealed class WorldRoutedPresentationLawTests {
    private const string Away = "north";
    private const string Home = "boot";

    private static readonly Vector3 HomePose = new(x: 1f, y: 0f, z: 1f);
    private static readonly Vector3 AwayPose = new(x: 5f, y: 0f, z: 7f);

    private sealed class Lease : IDisposable {
        public void Dispose() {
        }
    }
    private sealed class NoNeighbours : IWorldAdjacencySource {
        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;

            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [];
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;

            return false;
        }
    }

    // One active body at index 0, posed.
    private static WorldSnapshot Snapshot(string authority, Vector3 position) => new(
        Authority: authority,
        EngineTick: 1680UL,
        Entries: new[] { new EntitySnapshot(
            Active: true,
            BodyColor: Vector3.One,
            CatalogRig: 0,
            Continuity: EntityContinuity.Continuous,
            Generation: 1,
            Index: 0,
            Kit: 0,
            Look: 0,
            Orientation: Quaternion.Identity,
            Position: position
        ) },
        Revision: 0,
        StepTicks: 1680UL,
        Tick: 1UL
    );
    private static WorldAuthorityEndpoint Endpoint(string identity, WorldDefinition definition, Vector3 position) => new(
        adjacencies: static () => null,
        clockOwnedHere: false,
        definition: () => definition,
        identity: identity,
        nextInputTick: static () => 2UL,
        observe: sink => {
            sink.DeliverDefinition(definition: definition);
            sink.DeliverSnapshot(snapshot: Snapshot(
                authority: identity,
                position: position
            ));

            return new Lease();
        },
        submissions: new SilentLink(definition: definition)
    );
    // The destination runs a document of its own, and stands a door the home world has not.
    private static WorldDefinition AwayDocument() => (Fixtures.BuildDocument() with {
        CreationsRaw = [PortalArrivalValidationLawTests.BuildDoorCreation()],
        DocumentId = "away",
        PlacementRowsRaw = [new WorldPlacement(
            Id: "door",
            Position: new Vector3(x: 2f, y: 0f, z: -4f),
            PrototypeId: "door",
            Scale: 2f,
            YawDegrees: 0f
        )],
    });
    private static SdfFrame Capture(ISdfFrameSource source) => source.CaptureFrame(
        deltaSeconds: 0f,
        height: 36U,
        interpolationAlpha: 0f,
        width: 64U
    );

    [Fact]
    public void SplitSeatsInTwoWorldsArePresentedEachInItsOwn() {
        var home = (Fixtures.BuildDocument() with { DocumentId = "home" });
        var away = AwayDocument();
        var client = ClientFixtures.Client(definition: home);
        var routes = new WorldSeatAuthorityRouter();

        client.DeliverSnapshot(snapshot: Snapshot(
            authority: Home,
            position: HomePose
        ));
        client.UpdateRenderPoses(alpha: 1f);

        using var boot = Endpoint(
            definition: home,
            identity: Home,
            position: HomePose
        );
        using var north = Endpoint(
            definition: away,
            identity: Away,
            position: AwayPose
        );

        _ = routes.Publish(
            endpoint: boot,
            entity: client.EntityAddress(index: 0),
            slot: 0
        );
        _ = routes.Publish(
            endpoint: north,
            entity: new WorldEntityAddress(
                Authority: Away,
                Generation: 1,
                Index: 0
            ),
            slot: 1
        );

        var continuum = new WorldContinuum(
            client,
            routes,
            new NoNeighbours()
        );

        Assert.Null(@object: continuum.PresentedElsewhere(slot: 0));
        Assert.Same(
            actual: continuum.PresentedElsewhere(slot: 1),
            expected: north
        );

        // Seat 1 has its pose in the boot frame; seat 2 has none there, and its presented pose is the destination's own.
        Assert.True(condition: continuum.TryResolveSeatPose(
            interpolationAlpha: 1f,
            orientation: out _,
            position: out var homePosition,
            slot: 0
        ));
        Assert.Equal(
            actual: homePosition,
            expected: HomePose
        );
        Assert.False(condition: continuum.TryResolveSeatPose(
            interpolationAlpha: 1f,
            orientation: out _,
            position: out _,
            slot: 1
        ));
        Assert.True(condition: continuum.TryResolvePresentedSeatPose(
            interpolationAlpha: 1f,
            orientation: out _,
            position: out var awayPosition,
            slot: 1
        ));
        Assert.Equal(
            actual: awayPosition,
            expected: AwayPose
        );
    }
    [Fact]
    public void ARoutedSeatsViewEmitsItsDestinationsDefinition() {
        var home = (Fixtures.BuildDocument() with { DocumentId = "home" });

        using var north = Endpoint(
            definition: AwayDocument(),
            identity: Away,
            position: AwayPose
        );

        var scene = new WorldRoutedScene(
            bodyColor: north.Mirror.BodyColor,
            endpoint: north,
            hostFrame: static () => null
        );
        var view = new SdfViewSnapshot(
            Camera: CameraSnapshot.LookAt(
                fieldOfViewRadians: 1f,
                position: (AwayPose + new Vector3(x: 0f, y: 2f, z: 4f)),
                target: AwayPose,
                viewportHeight: 36U,
                viewportWidth: 64U
            ),
            Region: new NormalizedRect(
                Height: 1f,
                Width: 1f,
                X: 0f,
                Y: 0f
            )
        );

        Assert.Equal(
            actual: scene.AddView(view: in view),
            expected: 0
        );

        var frame = Capture(source: scene.FrameSource);
        var destination = new WorldSessionSceneEmitter(
            effectiveCameraName: null,
            mirror: north.Mirror
        );
        var boot = new WorldSessionSceneEmitter(
            effectiveCameraName: null,
            mirror: new WorldSessionMirror(placeholder: home)
        );
        var destinationProgram = Capture(source: new SdfCompositionFrameSource(
            dresser: destination,
            emitters: [destination]
        )).Program;
        var bootProgram = Capture(source: new SdfCompositionFrameSource(
            dresser: boot,
            emitters: [boot]
        )).Program;

        Assert.Equal(
            actual: Assert.Single(collection: frame.Views),
            expected: view
        );
        Assert.True(condition: frame.Program.Words.SequenceEqual(other: destinationProgram.Words));
        Assert.False(condition: frame.Program.Words.SequenceEqual(other: bootProgram.Words));
    }
}
