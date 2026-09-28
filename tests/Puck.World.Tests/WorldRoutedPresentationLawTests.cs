using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Testing;
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
public sealed partial class WorldRoutedPresentationLawTests {
    private const string Away = "north";
    private const string Home = "boot";

    private static readonly Vector3 HomePose = new(x: 1f, y: 0f, z: 1f);
    private static readonly Vector3 AwayPose = new(x: 5f, y: 0f, z: 7f);

    private sealed class Lease : IDisposable {
        public void Dispose() {
        }
    }
    private sealed class NoNeighbours : IWorldAdjacencySource {
        public IReadOnlyList<WorldAdjacencyProjection> Projections { get; init; } = [];

        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;

            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => Projections;
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
    public void ARoutePublishedDuringCaptureWaitsForTheNextFramesPresentationDecision() {
        var home = (Fixtures.BuildDocument() with { DocumentId = "home" });
        var routes = new WorldSeatAuthorityRouter();
        using var boot = Endpoint(definition: home, identity: Home, position: HomePose);
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        var homeEntity = boot.Mirror.Address(index: 0);
        var awayEntity = north.Mirror.Address(index: 0);
        var original = routes.Publish(endpoint: boot, entity: homeEntity, slot: 0);
        var continuum = new WorldContinuum(adjacencies: new NoNeighbours(), client: ClientFixtures.Client(definition: home), routes: routes);

        continuum.BeginFrame();
        var revision = continuum.Revision;
        var presentation = continuum.PresentationRevision;

        _ = routes.Publish(endpoint: north, entity: awayEntity, slot: 0);
        Assert.Same(actual: continuum.Route(slot: 0), expected: original);
        Assert.Null(@object: continuum.PresentedElsewhere(slot: 0));
        Assert.Equal(actual: continuum.Revision, expected: revision);
        Assert.True(condition: continuum.IsFollowed(entity: in homeEntity));
        Assert.False(condition: continuum.IsFollowed(entity: in awayEntity));
        continuum.EndFrame();
        continuum.BeginFrame();
        Assert.Same(actual: continuum.PresentedElsewhere(slot: 0), expected: north);
        Assert.NotEqual(actual: continuum.PresentationRevision, expected: presentation);
        continuum.EndFrame();
    }
    [Fact]
    public void MixedLayoutsLatchViewIndicesAndRetireScenesWhenSeatsReturnHome() {
        using var state = new TemporaryDirectory(prefix: "puck-routed-layout-");
        using var host = WorldBootHarness.Compose(
            edit: definition => (definition with {
                ViewsRaw = (definition.Views with {
                    Graphs = [new WorldViewGraph(Name: "pane", Package: "sdf.world")],
                    Layouts = [new WorldViewLayout(Name: "mixed", Slots: [
                        new WorldViewSlot(Camera: definition.Cameras[0].Name, Height: 1f, Width: 0.2f, X: 0f, Y: 0f),
                        new WorldViewSlot(Height: 1f, Instance: "pane", Width: 0.2f, X: 0.2f, Y: 0f),
                        new WorldViewSlot(Height: 1f, Width: 0.2f, X: 0.4f, Y: 0f),
                        new WorldViewSlot(Camera: definition.Cameras[0].Name, Height: 1f, Width: 0.2f, X: 0.6f, Y: 0f),
                        new WorldViewSlot(Height: 1f, Width: 0.2f, X: 0.8f, Y: 0f),
                    ])],
                }),
            }),
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: state,
            world: "tests/Puck.Counters/counters.world.json"
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var routes = host.Services.GetRequiredService<WorldSeatAuthorityRouter>();

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            _ = client.Roster.VacateSeat(slot: slot);
        }
        _ = client.Roster.OccupySeat(profile: null, slot: 0);
        _ = client.Roster.OccupySeat(profile: null, slot: 1);
        using var boot = Endpoint(definition: client.Definition, identity: Home, position: HomePose);
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);

        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        _ = routes.Publish(endpoint: boot, entity: boot.Mirror.Address(index: 0), slot: 1);
        var frame = Capture(source: presenter);

        Assert.Equal(actual: frame.Views.Count, expected: 4);
        Assert.True(condition: presenter.TryRoutedView(index: out var index, scene: out var scene, view: 1));
        Assert.Equal(actual: index, expected: 0);
        Assert.Equal(actual: Capture(source: scene!.FrameSource).Views[0], expected: frame.Views[1]);
        foreach (var view in new[] { -1, 0, 2, 3, 4, int.MaxValue }) {
            Assert.False(condition: presenter.TryRoutedView(index: out _, scene: out _, view: view));
        }
        _ = routes.Publish(endpoint: boot, entity: boot.Mirror.Address(index: 0), slot: 0);
        _ = Capture(source: presenter);
        Assert.False(condition: presenter.Presents(scene: scene));
        Assert.False(condition: presenter.TryRoutedView(index: out _, scene: out _, view: 1));

        _ = client.Roster.VacateSeat(slot: 0);
        _ = client.Roster.VacateSeat(slot: 1);
        client.DeliverDefinition(definition: (client.Definition with { ViewsRaw = (client.Definition.Views with { Layouts = [] }) }));
        _ = Capture(source: presenter);
        Assert.Single(collection: presenter.CaptureFrame(deltaSeconds: 1f, height: 36U, interpolationAlpha: 1f, width: 64U).Views);
        Assert.False(condition: presenter.TryRoutedView(index: out _, scene: out _, view: 0));
    }
    [Fact]
    public void ARoutedPaletteFollowsHostColorsWithoutAnAuthorityDelivery() {
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        var color = Vector3.UnitX;
        var scene = new WorldRoutedScene(bodyColor: _ => color, endpoint: north, hostFrame: static () => null);
        var first = Capture(source: scene.FrameSource);

        color = Vector3.UnitY;
        var second = Capture(source: scene.FrameSource);

        Assert.NotSame(actual: second.Program, expected: first.Program);
        Assert.False(condition: first.Program.Words.SequenceEqual(other: second.Program.Words));
        var steady = Capture(source: scene.FrameSource);

        Assert.Same(actual: steady.Program, expected: second.Program);
    }

    private sealed class Neighbour(WorldDefinition definition) : IWorldAdjacencyNeighbour {
        public string Authority => Away;
        public WorldFaceFrame CounterpartFrame => default;
        public WorldDefinition Definition => definition;
        public int DefinitionRevision => 0;
        public int EntityCapacity => 1;
        public float InterpolationAlpha => 1f;
        public int SnapshotRevision => 0;
        public ulong SnapshotTick => 1;

        public bool IsEntityActive(int index) => (index == 0);
        public WorldEntityAddress EntityAddress(int index) => new(Authority: Away, Generation: 1, Index: index);
        public Vector3 PreviousPosition(int index) => AwayPose;
        public Quaternion PreviousOrientation(int index) => Quaternion.Identity;
        public Vector3 CurrentPosition(int index) => AwayPose;
        public Quaternion CurrentOrientation(int index) => Quaternion.Identity;
        public Vector3 BodyColor(int index) => Vector3.One;
        public WorldLook Look(int index) => WorldLook.Implicit;
        public byte CatalogRig(int index) => 0;
        public FixedWorldCollider? Collider(int index) => null;
        public WorldBodyContactMode BodyContact(int index) => WorldBodyContactMode.Solid;
    }

    [Fact]
    public void AProjectionOfAnotherDocumentDoesNotKeepTheSeatInTheBootFrame() {
        var home = (Fixtures.BuildDocument() with { DocumentId = "home" });
        var routes = new WorldSeatAuthorityRouter();
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);

        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        var continuum = new WorldContinuum(
            adjacencies: new NoNeighbours {
                Projections = [new WorldAdjacencyProjection(
                    Direct: true,
                    Name: "neighbour",
                    Neighbour: new Neighbour(definition: home),
                    OverlapDepth: default,
                    Path: []
                )],
            },
            client: ClientFixtures.Client(definition: home),
            routes: routes
        );

        Assert.Same(actual: continuum.PresentedElsewhere(slot: 0), expected: north);
        Assert.False(condition: continuum.TryResolveSeatPose(interpolationAlpha: 1f, orientation: out _, position: out _, slot: 0));
        Assert.True(condition: continuum.TryResolvePresentedSeatPose(interpolationAlpha: 1f, orientation: out _, position: out var position, slot: 0));
        Assert.Equal(actual: position, expected: AwayPose);
    }
    [Fact]
    public void ChangingTheViewWithinOneResidencyPreservesItsPasses() {
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        var scene = new WorldRoutedScene(bodyColor: north.Mirror.BodyColor, endpoint: north, hostFrame: static () => null);
        using var residency = new SdfWorldResidency(
            film: static _ => false,
            frameSource: scene.FrameSource,
            height: 36U,
            kernels: SdfTestPipelines.Kernels(),
            name: Away,
            pipelines: SdfTestPipelines.Cache(),
            width: 64U
        );
        var index = 1;
        var passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: index));
        var counter = passes.CounterOf(instance: "world$2")!;
        var revision = counter.Revision;

        // Another view of the same residency is one the passes record as built: they follow it, so the counter stays
        // put. SdfWorldPassesLawTests also records both bindings and checks when the instance may stand.
        index = 0;
        passes.BeginFrame(context: default);
        Assert.Same(actual: passes.CounterOf(instance: "world$2"), expected: counter);
        Assert.Equal(actual: counter.Revision, expected: revision);
        passes.BeginFrame(context: default);
        _ = passes.CounterOf(instance: "world$2");
        Assert.Equal(actual: counter.Revision, expected: revision);
    }

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => frame;
    }

    [Fact]
    public void AnInstanceCannotStandOnAViewRenderedByAnotherInstance() {
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        var scene = new WorldRoutedScene(bodyColor: north.Mirror.BodyColor, endpoint: north, hostFrame: static () => null);
        var frame = (Capture(source: scene.FrameSource) with { EnableCadenceGate = true });
        var gpu = new FakeGpuDevice();
        var context = new FrameContext(
            AccumulatorTicks: 0UL,
            DeltaTicks: 0UL,
            ElapsedTicks: 0UL,
            FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0UL,
            TargetHeight: 36U,
            TargetWidth: 64U
        );
        using var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame),
            height: 36U,
            kernels: SdfTestPipelines.Kernels(),
            name: Away,
            pipelines: SdfTestPipelines.Cache(),
            width: 64U
        );

        residency.ProduceFirstFrame(context: in context);
        residency.MarkRendered(view: 0);
        Assert.True(condition: residency.IsUnchanged(context: in context, view: 0));
        var passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));

        Assert.False(condition: passes.IsUnchanged(context: in context, instance: "world"));
    }

    private sealed class SilentAudio : IWorldAudioCueSink {
        public void SubmitCue(string eventToken, Vector3? site) { }
    }

    [Fact]
    public void ADepartedSeatIsAbsentEvenWhileTheBootSnapshotStillContainsIt() {
        var home = (Fixtures.BuildDocument() with { DocumentId = "home" });
        var client = ClientFixtures.Client(definition: home);
        var routes = new WorldSeatAuthorityRouter();

        Assert.True(condition: client.Roster.OccupySeat(profile: null, slot: 0));
        client.DeliverSnapshot(snapshot: Snapshot(authority: Home, position: HomePose));
        client.UpdateRenderPoses(alpha: 1f);
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);

        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        var emitter = new WorldSceneEmitter(
            anchor: new WorldPerceptionAnchor(),
            animator: new WorldStampPool(),
            audio: new SilentAudio(),
            client: client,
            continuum: new WorldContinuum(adjacencies: new NoNeighbours(), client: client, routes: routes),
            settings: new WorldRenderSettings(defaults: home.Render),
            text: new WorldTextCatalog(source: new(Definition: home, SourcePath: "unused.world.json"))
        );
        var dresser = new WorldSessionSceneEmitter(effectiveCameraName: null, mirror: new WorldSessionMirror(placeholder: home));
        var source = new SdfCompositionFrameSource(dresser: dresser, emitters: [emitter]);
        var withDepartedSeat = Capture(source: source).Program;

        client.DeliverSnapshot(snapshot: (Snapshot(authority: Home, position: HomePose) with { Entries = ReadOnlyMemory<EntitySnapshot>.Empty, Revision = 1 }));
        client.UpdateRenderPoses(alpha: 1f);
        var withoutDepartedSeat = Capture(source: source).Program;

        Assert.True(condition: withDepartedSeat.Words.SequenceEqual(other: withoutDepartedSeat.Words));
    }
    [Fact]
    public void ASameDocumentAuthorityKeepsItsPoseInTheBootFrame() {
        var home = Fixtures.BuildDocument();
        var client = ClientFixtures.Client(definition: home);
        var routes = new WorldSeatAuthorityRouter();
        using var north = Endpoint(definition: home, identity: Away, position: AwayPose);

        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        var continuum = new WorldContinuum(adjacencies: new NoNeighbours(), client: client, routes: routes);

        Assert.Null(@object: continuum.PresentedElsewhere(slot: 0));
        Assert.True(condition: continuum.TryResolveSeatPose(interpolationAlpha: 1f, orientation: out _, position: out var position, slot: 0));
        Assert.Equal(actual: position, expected: AwayPose);
    }
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
        scene.EndViews();

        var frame = Capture(source: scene.FrameSource);

        Assert.True(condition: frame.DynamicTransforms[0].CastsSoftShadow);
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
