using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Sources;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

// The views the world renders beside its own, as the host composes and the scheduler feeds them: a camera view is one
// sdf.world instance however many screens show it, rendered once a frame while a screen shows it, never while nothing
// does, at the extent its footprint asks, reading itself at its previous frame; and a hit on a screen showing it
// continues into the view.
public sealed partial class WorldViewPaneMappingLawTests {
    private const string ViewCamera = "cam";

    private static WorldScreen FacingScreen(int index, WorldScreenSource source) => new(
        HalfDepth: 0.1f,
        HalfHeight: 0.9f,
        HalfWidth: 1.2f,
        Index: index,
        Origin: new DocumentVector3(value: new Vector3(x: 0f, y: 1f, z: 0f)),
        Right: new DocumentVector3(value: Vector3.UnitX),
        Round: 0f,
        Route: WorldScreenRoute.Passive,
        Source: source,
        Up: new DocumentVector3(value: Vector3.UnitY)
    );
    private static WorldCamera FilmingCamera() => new(
        Anchor: null,
        Name: ViewCamera,
        RenderHeight: 72U,
        RenderWidth: 128U,
        Rig: new WorldCameraProgram(
            Name: $"{ViewCamera}-rig",
            Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0.9f))],
            Version: WorldCameraProgram.CurrentVersion
        )
    );
    // Two screens showing the camera, and the camera as a view of the given demand at a quarter of the display.
    private WorldScreenMappingSet ShowCamera(WorldViewDemand demand) {
        var screens = new WorldScreenMappingSet(world: WorldDefinitionLoader.BootInstanceName);
        var view = new WorldScreenSource.View(CameraName: ViewCamera);

        screens.Reconcile(
            cameras: [FilmingCamera()],
            screens: [
                FacingScreen(index: 0, source: view),
                FacingScreen(index: 1, source: view),
            ]
        );
        screens.ReconcileViews(views: WorldViewInstances.Of(views: [new WorldView(
            Demand: demand,
            FilmsWorld: true,
            Height: 0.25,
            Name: ViewCamera,
            Refresh: RenderGraphRefresh.EveryFrame,
            Width: 0.25
        )]));
        screens.Publish(images: new PatternImages(Height: 1, Width: 1));
        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Left));
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 3f), Region: Right));

        return screens;
    }
    // How many of the last frame's renders were of an instance.
    private int RendersOf(string instance) {
        var index = m_instances.Instances.IndexOf(name: instance);

        return m_instances.Latest!.Renders.Count(predicate: render => (render == index));
    }

    [Fact]
    public void ACameraOnTwoScreensIsOneInstanceRenderedOnceAFrame() {
        _ = ShowCamera(demand: WorldViewDemand.Screen);

        for (var frame = 0; (frame < 3); frame++) {
            Frame(pane: null);
        }

        var set = m_instances.Instances;

        Assert.Single(collection: set.Instances, predicate: static instance => (instance.Name == ViewCamera));
        Assert.Equal(
            actual: RendersOf(instance: ViewCamera),
            expected: 1
        );
        // The world shows the camera's image of this frame, and the camera itself its own previous one.
        var world = set.IndexOf(name: WorldViewGraphs.WorldInstance);
        var camera = set.IndexOf(name: ViewCamera);

        Assert.Contains(collection: set.Reads[world], filter: edge => ((edge.Producer == camera) && !edge.PreviousFrame));
        Assert.Contains(collection: set.Reads[camera], filter: edge => ((edge.Producer == camera) && edge.PreviousFrame));
        Assert.True(condition: (set.Order.ToList().IndexOf(item: camera) < set.Order.ToList().IndexOf(item: world)));
    }
    // A camera a screen and a HUD frame both show is demanded both ways: the world shows it through a footprint, and the
    // display shows it as a root, so it renders even where nothing schedules the world.
    [Fact]
    public void ACameraAScreenAndAHudFrameShowIsAFootprintAndARoot() {
        _ = ShowCamera(demand: WorldViewDemand.Screen | WorldViewDemand.Root);
        Frame(pane: null);

        Assert.Contains(
            collection: m_host.Footprints,
            filter: static footprint => (footprint.Producer == ViewCamera)
        );
        Assert.Equal(
            actual: Assert.Single(collection: m_host.Roots).Instance,
            expected: ViewCamera
        );
    }
    [Fact]
    public void ACameraNothingShowsRendersZeroTimes() {
        _ = ShowCamera(demand: WorldViewDemand.None);

        for (var frame = 0; (frame < 4); frame++) {
            Frame(pane: null);
            Assert.Equal(
                actual: RendersOf(instance: ViewCamera),
                expected: 0
            );
        }
    }
    [Fact]
    public void AViewOnAQuarterScreenRendersAtAQuarterExtent() {
        _ = ShowCamera(demand: WorldViewDemand.Screen);
        Frame(pane: null);
        Frame(pane: null);

        var latest = m_instances.Latest!;
        var world = latest.Instances[m_instances.Instances.IndexOf(name: WorldViewGraphs.WorldInstance)];
        var camera = latest.Instances[m_instances.Instances.IndexOf(name: ViewCamera)];

        Assert.Equal(
            actual: (camera.Width, camera.Height),
            expected: ((world.Width / 4), (world.Height / 4))
        );
    }
    [Fact]
    public void AHitOnAScreenShowingACameraContinuesIntoTheView() {
        var screens = ShowCamera(demand: WorldViewDemand.Screen);

        m_host.ViewScenes = new FixedViewScenes(Camera: Camera(x: 0f));
        Frame(pane: null);
        Frame(pane: null);

        var set = m_instances.Instances;
        var walk = m_host.Walk(point: Point(x: 16.5, y: 32.5))!;

        Assert.NotEqual(
            actual: walk.End,
            expected: RenderGraphHitEnd.Unread
        );
        Assert.True(condition: (walk.Steps.Count >= 3));
        Assert.Equal(
            actual: walk.Steps[1].Mapping.Source,
            expected: SourceHandle.Instance(name: ViewCamera)
        );
        Assert.Equal(
            actual: walk.Steps[2].Instance,
            expected: set.IndexOf(name: ViewCamera)
        );
        Assert.Contains(
            collection: screens.Mappings,
            filter: mapping => (mapping == walk.Steps[2].Mapping)
        );
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void OnlyARootCameraDemandsTheScreensItFilmsWhileTheWorldIsHidden(bool filmsWorld) {
        var screens = ShowCamera(demand: WorldViewDemand.Root);

        screens.Reconcile(
            cameras: [FilmingCamera()],
            screens: [FacingScreen(index: 0, source: new WorldScreenSource.Probe(Id: "feed"))]
        );
        screens.ReconcileViews(views: WorldViewInstances.Of(views: [
            new WorldView(Demand: WorldViewDemand.Root, FilmsWorld: filmsWorld, Height: 0.25, Name: ViewCamera, Refresh: RenderGraphRefresh.EveryFrame, Width: 0.25),
            new WorldView(Demand: WorldViewDemand.Screen, FilmsWorld: false, Height: 0.25, Name: "session", Refresh: RenderGraphRefresh.EveryFrame, Width: 0.25),
        ]));
        Prepare(pane: null);

        var set = m_instances.Instances;
        var source = Assert.Single(collection: screens.Sources.Instances).Name;
        var schedule = new RenderGraphSchedule(set: set);

        RenderGraphScheduler.Schedule(
            frame: new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: m_host.Footprints,
                Index: 0,
                Roots: m_host.Roots,
                Sources: [new RenderGraphSourceState(Cadence: ImageSourceCadence.Tick, Height: Display, Instance: source, Width: Display)]
            ),
            history: RenderGraphHistory.Empty(set: set),
            schedule: schedule,
            set: set
        );

        Assert.Contains(expected: set.IndexOf(name: ViewCamera), collection: schedule.Renders);
        Assert.DoesNotContain(expected: set.IndexOf(name: WorldViewGraphs.WorldInstance), collection: schedule.Renders);
        Assert.Equal(expected: filmsWorld, actual: schedule.Renders.Contains(value: set.IndexOf(name: source)));
        Assert.Equal(expected: filmsWorld, actual: schedule.Renders.Contains(value: set.IndexOf(name: "session")));
    }
    // A filming camera's demand of the views it films is the host's rooting of them, which goes with the camera's own
    // root: once nothing shows the camera, nothing demands the views it filmed, so they stop rendering.
    [Fact]
    public void TheViewsAFilmingCameraRootsStopRenderingOnceTheCameraIsNoLongerShown() {
        var screens = ShowCamera(demand: WorldViewDemand.Root);

        screens.Reconcile(
            cameras: [FilmingCamera()],
            screens: [FacingScreen(index: 0, source: new WorldScreenSource.Probe(Id: "feed"))]
        );
        foreach (var demand in ((WorldViewDemand[])[WorldViewDemand.Root, WorldViewDemand.None, WorldViewDemand.Root])) {
            var shown = (demand == WorldViewDemand.Root);

            screens.ReconcileViews(views: WorldViewInstances.Of(views: [
                new WorldView(Demand: demand, FilmsWorld: true, Height: 0.25, Name: ViewCamera, Refresh: RenderGraphRefresh.EveryFrame, Width: 0.25),
                new WorldView(Demand: WorldViewDemand.Screen, FilmsWorld: false, Height: 0.5, Name: "session", Refresh: RenderGraphRefresh.EveryFrame, Width: 0.5),
            ]));
            Prepare(pane: null);

            var set = m_instances.Instances;
            var source = Assert.Single(collection: screens.Sources.Instances).Name;
            var schedule = new RenderGraphSchedule(set: set);

            RenderGraphScheduler.Schedule(
                frame: new RenderGraphFrame(
                    DisplayHeight: Display,
                    DisplayHertz: 60,
                    DisplayWidth: Display,
                    Footprints: m_host.Footprints,
                    Index: 0,
                    Roots: m_host.Roots,
                    Sources: [new RenderGraphSourceState(Cadence: ImageSourceCadence.Tick, Height: Display, Instance: source, Width: Display)]
                ),
                history: RenderGraphHistory.Empty(set: set),
                schedule: schedule,
                set: set
            );

            Assert.Equal(expected: shown, actual: schedule.Renders.Contains(value: set.IndexOf(name: ViewCamera)));
            Assert.Equal(expected: shown, actual: schedule.Renders.Contains(value: set.IndexOf(name: "session")));
            RenderGraphRoot[] rooted = (shown ? [new RenderGraphRoot(Height: 0.125, Instance: "session", Width: 0.125)] : []);

            Assert.Equal(
                expected: rooted,
                actual: m_host.Roots.Where(predicate: static root => (root.Instance == "session")).ToArray()
            );
        }
    }
    // A filmed view renders no more often than the root camera filming it can consume it: with the world hidden, a window
    // session refreshed every frame, filmed by a camera refreshed every third frame, renders once per the camera's
    // period, as the camera's demand of it did before a previous-frame read stopped demanding anything.
    [Fact]
    public void AFilmedViewRendersOncePerTheRootCamerasRefreshPeriod() {
        var screens = ShowCamera(demand: WorldViewDemand.Root);

        screens.Reconcile(
            cameras: [FilmingCamera()],
            screens: [FacingScreen(index: 0, source: new WorldScreenSource.Probe(Id: "feed"))]
        );
        screens.ReconcileViews(views: WorldViewInstances.Of(views: [
            new WorldView(Demand: WorldViewDemand.Root, FilmsWorld: true, Height: 0.25, Name: ViewCamera, Refresh: RenderGraphRefresh.Every(divisor: 3), Width: 0.25),
            new WorldView(Demand: WorldViewDemand.Screen, FilmsWorld: false, Height: 0.5, Name: "session", Refresh: RenderGraphRefresh.EveryFrame, Width: 0.5),
        ]));
        Prepare(pane: null);

        var set = m_instances.Instances;
        var source = Assert.Single(collection: screens.Sources.Instances).Name;
        var history = RenderGraphHistory.Empty(set: set);

        var (cameras, sessions) = (0, 0);

        for (var frame = 0; (frame < 12); frame++) {
            var schedule = new RenderGraphSchedule(set: set);

            RenderGraphScheduler.Schedule(
                frame: new RenderGraphFrame(
                    DisplayHeight: Display,
                    DisplayHertz: 60,
                    DisplayWidth: Display,
                    Footprints: m_host.Footprints,
                    Index: frame,
                    Roots: m_host.Roots,
                    Sources: [new RenderGraphSourceState(Cadence: ImageSourceCadence.Tick, Height: Display, Instance: source, Width: Display)]
                ),
                history: history,
                schedule: schedule,
                set: set
            );
            cameras += (schedule.Renders.Contains(value: set.IndexOf(name: ViewCamera)) ? 1 : 0);
            sessions += (schedule.Renders.Contains(value: set.IndexOf(name: "session")) ? 1 : 0);
            history = schedule.Next;
        }
        Assert.Equal(actual: (cameras, sessions), expected: (4, 4));
    }

    private sealed record FixedViewScenes(CameraSnapshot Camera) : IWorldViewScenes {
        public bool TryCamera(string view, out CameraSnapshot camera) {
            camera = Camera;

            return string.Equals(
                a: view,
                b: ViewCamera,
                comparisonType: StringComparison.Ordinal
            );
        }
        public bool TrySurface(string view, SourceRay ray, out FixedVector3 point) {
            point = default;

            return false;
        }
        public bool TryPlacements(string view, out IReadOnlyList<SourceMapping> placements) {
            placements = [];

            return false;
        }
        public WorldPortalGlass PortalGlass(string consumer, string producer, out WorldScreen? glass) {
            glass = null;

            return WorldPortalGlass.None;
        }
    }
}
