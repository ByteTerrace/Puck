using Puck.Abstractions.Cameras;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

// The world.rates screens lever (SdfUpdateRates.Screens): each view's footprint of a source the boot world's screens show
// asks for the refresh its largest on-screen size of the source's image calls for, and every frame while the lever is
// off, the view's camera is unknown or the image is shown at or above its own size. A sky environment reads the source at
// the most frequent refresh of the views it lights.
public sealed partial class WorldViewPaneMappingLawTests {
    private const string FirstView = "world";
    private const string SecondView = "world$2";
    private const string UnplacedView = "world$3";

    // Shows a test pattern source `sourceHeight` pixels high on two screens, one facing each of two placed views, and
    // frames the host twice under the lever, so its footprints rate by the shares the first frame measured. Returns the
    // frames of a 60 Hz display each consumer's footprint of the source holds the source for (a floor of 15 Hz holds
    // four, every frame one), by consumer, and how many footprints the lever counted rated. A root camera view films the
    // world beside the placed views when `rootCamera` is set.
    private (Dictionary<string, int> Divisors, long Rated) ScreenDivisors(SdfUpdateRates rates, int sourceHeight, IWorldViewScenes? scenes, bool placeSecond = true, bool rootCamera = false, bool farSecond = false, SdfWorldPasses? pickers = null, SdfSkyEnvironmentPasses? environment = null) {
        var pattern = WorldImageProducerSettings.SourceOf(
            id: WorldImageProducerSettings.TestPatternId,
            settings: new WorldTestPatternSettings(
                Height: sourceHeight,
                Width: sourceHeight
            )
        );
        var screens = new WorldScreenMappingSet(world: WorldDefinitionLoader.BootInstanceName);
        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults()) { UpdateRates = rates };

        screens.Reconcile(
            cameras: (rootCamera ? [FilmingCamera()] : []),
            screens: [
                FacingScreen(index: 0, source: pattern),
                FacingScreen(index: 1, source: pattern) with { Origin = new DocumentVector3(value: new System.Numerics.Vector3(x: 3f, y: 1f, z: 0f)) },
            ]
        );

        if (rootCamera) {
            screens.ReconcileViews(views: WorldViewInstances.Of(views: [new WorldView(
                Demand: WorldViewDemand.Root,
                FilmsWorld: true,
                Height: 0.5,
                Name: ViewCamera,
                Refresh: RenderGraphRefresh.EveryFrame,
                Width: 0.5
            )]));
        }

        screens.Publish(images: new PatternImages(Height: sourceHeight, Width: sourceHeight));
        m_host.Screens = screens;
        m_host.RenderSettings = settings;
        m_host.ViewScenes = scenes;
        m_host.Pickers = pickers;
        m_host.Environment = environment;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Left));
        if (placeSecond) {
            m_views.Add(item: new SdfViewSnapshot(Camera: (farSecond ? CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new System.Numerics.Vector3(x: 3f, y: 1f, z: 20f), target: new System.Numerics.Vector3(x: 3f, y: 1f, z: 0f), viewportHeight: Display, viewportWidth: Display) : Camera(x: 3f)), Region: Right));
        }

        m_rate = Paced60;
        Frame(pane: null);
        Frame(pane: null);

        var source = screens.InstanceOf(screen: 0);

        return (
            m_host.Footprints.Where(predicate: footprint => (footprint.Producer == source)).ToDictionary(keySelector: static footprint => footprint.Consumer, elementSelector: static footprint => ((footprint.Refresh.Floor > 0) ? (60 / footprint.Refresh.Floor) : footprint.Refresh.Divisor)),
            settings.RateCounts.Read(kind: SdfUpdateRateCounts.ScreensRated)
        );
    }

    // A screen's image shown well below its own size is asked for at 15 Hz, every fourth frame of a 60 Hz display, by each
    // view that stands in the boot world, or in another world whose image the glass shows no larger than; a view whose
    // camera is unknown asks every frame. The lever off asks every frame of all of them.
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [Theory]
    public void AScreenImageShownSmallIsAskedForAtAReducedRateWhileTheScreensLeverIsOn(bool lever, bool elsewhere) {
        var (divisors, rated) = ScreenDivisors(
            rates: (lever ? SdfUpdateRates.Screens : SdfUpdateRates.None),
            scenes: new SightScenes(Elsewhere: elsewhere),
            sourceHeight: 512
        );

        Assert.Equal(expected: (lever ? 4 : 1), actual: divisors[FirstView]);
        Assert.Equal(expected: (lever ? 4 : 1), actual: divisors[SecondView]);
        // A view the display reached no share of asks every frame.
        Assert.Equal(expected: 1, actual: divisors[UnplacedView]);
        Assert.Equal(expected: lever, actual: (rated > 0L));
    }
    // A view with a share of the display whose camera is not placed yet asks every frame, beside the views that are placed.
    [Fact]
    public void AViewWhoseCameraIsNotKnownYetAsksForAScreenImageEveryFrame() {
        var (divisors, _) = ScreenDivisors(
            rates: SdfUpdateRates.Screens,
            rootCamera: true,
            scenes: new SightScenes(Elsewhere: false),
            sourceHeight: 512
        );

        Assert.Equal(expected: 4, actual: divisors[FirstView]);
        Assert.Equal(expected: 1, actual: divisors[ViewCamera]);
    }
    // An image shown at its own size or more, and a host with no scenes to report a camera, ask every frame whatever the
    // lever.
    [InlineData("large")]
    [InlineData("scenes")]
    [Theory]
    public void AScreenImageShownLargeOrBeforeItsViewsAreKnownIsAskedForEveryFrame(string situation) {
        var (divisors, rated) = ScreenDivisors(
            rates: SdfUpdateRates.Screens,
            scenes: ((situation == "scenes") ? null : new SightScenes(Elsewhere: false)),
            sourceHeight: ((situation == "large") ? 1 : 512)
        );

        Assert.All(collection: divisors.Values, action: static divisor => Assert.Equal(expected: 1, actual: divisor));
        Assert.Equal(expected: 0L, actual: rated);
    }
    // A sky environment reads the source each screen in the views it lights shows, as often as the most frequent of those
    // views asks: its own footprint asks every frame until every view's is known, then takes the least divisor.
    [Fact]
    public void ASkyEnvironmentReadsAScreenImageAsOftenAsTheMostFrequentViewThatShowsIt() {
        var pipelines = SdfTestPipelines.Cache();
        using var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new UnusedFrameSource(),
            height: 4,
            kernels: SdfTestPipelines.Kernels(),
            name: "boot",
            pipelines: pipelines,
            width: 4
        );
        var passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        using var environment = new SdfSkyEnvironmentPasses(views: passes);
        var (divisors, _) = ScreenDivisors(
            environment: environment,
            farSecond: true,
            pickers: passes,
            rates: SdfUpdateRates.Screens,
            scenes: new SightScenes(Elsewhere: false),
            sourceHeight: 28
        );
        var views = new[] { divisors[FirstView], divisors[SecondView] };

        Assert.NotEqual(expected: views[0], actual: views[1]);
        Assert.Equal(expected: views.Min(), actual: divisors[WorldViewNames.Environment(residency: residency.Name)]);
    }

    // A frame source no frame is ever captured from.
    private sealed class UnusedFrameSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => throw new NotSupportedException();
    }
    // A scene whose views stand in the boot world, or all in another world, with no glass between them.
    private sealed record SightScenes(bool Elsewhere) : IWorldViewScenes {
        public bool TryCamera(string view, out CameraSnapshot camera) {
            camera = default;

            return false;
        }
        public bool TrySurface(string view, SourceRay ray, out FixedVector3 point) {
            point = default;

            return false;
        }
        public bool TryPlacements(string view, out IReadOnlyList<SourceMapping> placements) {
            placements = [];

            return Elsewhere;
        }
        public WorldPortalGlass PortalGlass(string consumer, string producer, out WorldScreen? glass) {
            glass = null;

            return WorldPortalGlass.None;
        }
        public bool Bakes(string view) => false;
    }
}
