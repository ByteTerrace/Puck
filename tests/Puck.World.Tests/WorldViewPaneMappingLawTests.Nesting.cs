using Puck.Abstractions.Cameras;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

// Each world a hit walks through is tested against its own screens, and a portal face no consumer sees schedules
// nothing beneath it.
public sealed partial class WorldViewPaneMappingLawTests {
    private const string BootSession = "session$0";
    private const string RoutedSession = "routed$away$0";

    // The boot world's one screen shows a session; a world a seat is presented in stands a screen at the same place
    // showing a session of its own. Both sessions are views a screen of a world the display shows directly shows.
    private WorldScreenMappingSet ShowTwoWorlds() {
        var screens = new WorldScreenMappingSet();

        screens.Reconcile(
            cameras: [],
            screens: [FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond"))]
        );
        screens.ReconcileViews(views: WorldViewInstances.Of(views: [
            new WorldView(Demand: WorldViewDemand.Screen, FilmsWorld: false, Height: 0.25, Name: BootSession, Refresh: RenderGraphRefresh.EveryFrame, Width: 0.25),
            new WorldView(Demand: WorldViewDemand.Screen, FilmsWorld: false, Height: 0.25, Name: RoutedSession, Refresh: RenderGraphRefresh.EveryFrame, Width: 0.25),
        ]));
        screens.Publish(images: new PatternImages(Height: 1, Width: 1));
        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Left));
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Right));

        return screens;
    }
    // The routed world's screen, as a scene reports it: the boot world's row, showing the routed world's session.
    private static SourceMapping RoutedScreen() => WorldScreenMappings.Of(
        screen: FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond")),
        source: SourceHandle.Instance(name: RoutedSession),
        sourceHeight: 1,
        sourceWidth: 1
    );

    // THE LAW: a seat presented in another world is hit-tested against that world's screens, never the boot world's. Its
    // view's producer, world$2, reports the routed world's screens, so a pick through the middle of the second view's
    // pane lands on the routed world's screen and continues toward its session; the first view, a seat at home, still
    // lands on the boot world's. The red leg is a scene that reports no other world's screens, the walk before
    // nesting: the second view then lands on the boot world's screen too, a session its seat cannot see.
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ASeatPresentedElsewhereIsHitTestedAgainstThatWorldsScreens(bool routes) {
        var screens = ShowTwoWorlds();

        m_host.ViewScenes = new RoutedScenes(Routes: routes);
        Frame(pane: null);
        Frame(pane: null);

        var home = m_host.Walk(point: Point(x: 16.5, y: 32.5))!;
        var away = m_host.Walk(point: Point(x: 48.5, y: 32.5))!;

        Assert.Equal(
            actual: home.Steps[1].Mapping.Source,
            expected: SourceHandle.Instance(name: BootSession)
        );
        Assert.Equal(
            actual: away.Steps[1].Instance,
            expected: m_instances.Instances.IndexOf(name: WorldViewNames.World(view: 2))
        );
        Assert.Equal(
            actual: away.Steps[1].Mapping.Source,
            expected: SourceHandle.Instance(name: (routes
                ? RoutedSession
                : BootSession))
        );
        Assert.Equal(
            actual: ((IRenderGraphHitScene)m_host).Placements(instance: m_instances.Instances.IndexOf(name: WorldViewNames.World(view: 2))),
            expected: (routes
                ? [RoutedScreen()]
                : screens.Mappings)
        );
    }
    // THE LAW: a session whose glass its consumer does not see renders nothing that frame. The boot world's session is
    // read by the world's view through the screen's glass; a scene placing that glass behind the view's camera keeps the
    // session out of the schedule, while one placing it before the camera schedules it.
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void APortalFaceNoConsumerSeesSchedulesNothing(bool facing) {
        _ = ShowTwoWorlds();
        m_host.ViewScenes = new GlassScenes(Glass: (facing
            ? FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond"))
            : (FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond")) with {
                Origin = new DocumentVector3(value: new System.Numerics.Vector3(x: 0f, y: 1f, z: 9f)),
            })));
        Frame(pane: null);
        Frame(pane: null);
        Frame(pane: null);

        Assert.Equal(
            actual: (RendersOf(instance: BootSession) != 0),
            expected: facing
        );
        Assert.Equal(
            actual: RendersOf(instance: RoutedSession),
            expected: 0
        );
    }

    // A scene that presents view 2's seat in another world, whose one screen shows the routed session; or, when it does
    // not route, one that reports no world but the boot world's.
    private sealed record RoutedScenes(bool Routes) : IWorldViewScenes {
        public bool TryCamera(string view, out CameraSnapshot camera) {
            camera = default;

            return false;
        }
        public bool TrySurface(string view, SourceRay ray, out FixedVector3 point) {
            point = default;

            return false;
        }
        public bool TryPlacements(string view, out IReadOnlyList<SourceMapping> placements) {
            placements = [RoutedScreen()];

            return (Routes && string.Equals(
                a: view,
                b: WorldViewNames.World(view: 2),
                comparisonType: StringComparison.Ordinal
            ));
        }
        public WorldPortalGlass PortalGlass(string consumer, string producer, out WorldScreen? glass) {
            glass = null;

            return WorldPortalGlass.None;
        }
    }
    // A scene whose boot session stands on a given glass in the boot world, and whose routed session stands in a world
    // no view shows.
    private sealed record GlassScenes(WorldScreen Glass) : IWorldViewScenes {
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

            return false;
        }
        public WorldPortalGlass PortalGlass(string consumer, string producer, out WorldScreen? glass) {
            glass = (string.Equals(a: producer, b: BootSession, comparisonType: StringComparison.Ordinal)
                ? Glass
                : null);

            return ((glass is null)
                ? WorldPortalGlass.Elsewhere
                : WorldPortalGlass.Found);
        }
    }
}
