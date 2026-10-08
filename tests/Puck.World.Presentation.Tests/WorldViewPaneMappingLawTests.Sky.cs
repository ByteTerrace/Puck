using Puck.Hosting;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

// The infinity views the world renders beside its own, as the host composes and the scheduler feeds them: the world's own
// instance reads every one within the frame, the graph shows one (a footprint) only while the presentation marks it seen,
// and an unseen one renders nothing and keeps what it has.
public sealed partial class WorldViewPaneMappingLawTests {
    private const string Infinity = "sky$lobby";

    // The infinity view as the presentation publishes it at a quarter of the display, with the given demand.
    private void ShowSky(WorldViewDemand demand) {
        var screens = new WorldScreenMappingSet(world: WorldDefinitionLoader.BootInstanceName);

        screens.Reconcile(cameras: [], screens: []);
        screens.ReconcileViews(views: WorldViewInstances.Of(views: [new WorldView(
            Demand: demand,
            FilmsWorld: false,
            Height: 0.25,
            Name: Infinity,
            Refresh: RenderGraphRefresh.EveryFrame,
            Width: 0.25
        ) { OutputExtent = new RenderGraphPixelExtent(Height: (Display / 4), Width: (Display / 4)) }]));
        screens.Publish(images: new PatternImages(Height: 1, Width: 1));
        m_host.Screens = screens;
    }

    [Fact]
    public void TheWorldReadsAnInfinityViewWithinTheFrameWhetherOrNotItIsSeen() {
        ShowSky(demand: WorldViewDemand.Sky);
        Frame(pane: null);

        var world = m_instances.Instances.Instances[m_instances.Instances.IndexOf(name: WorldViewGraphs.WorldInstance)];

        Assert.Contains(collection: world.Reads, filter: static read => ((read.Producer == Infinity) && !read.PreviousFrame));
    }
    [Fact]
    public void AnInfinityViewMarkedSeenIsAFootprintAndRendersEveryFrame() {
        ShowSky(demand: WorldViewDemand.Sky | WorldViewDemand.SkySeen);
        Frame(pane: null);

        // Every view of the world shows it, the first view's producer among them, and each at the view's footprint.
        var footprints = m_host.Footprints.Where(predicate: static footprint => (footprint.Producer == Infinity)).ToArray();

        Assert.Contains(collection: footprints, filter: static footprint => (footprint.Consumer == WorldViewGraphs.WorldInstance));
        Assert.All(collection: footprints, action: static footprint => Assert.Equal(expected: (0.25, 0.25), actual: (footprint.Width, footprint.Height)));

        for (var frame = 0; (frame < 3); frame++) {
            Frame(pane: null);
            Assert.Equal(expected: 1, actual: RendersOf(instance: Infinity));
        }
    }
    [Fact]
    public void AnInfinityViewNotSeenIsNoFootprintAndRendersNothing() {
        ShowSky(demand: WorldViewDemand.Sky);

        for (var frame = 0; (frame < 4); frame++) {
            Frame(pane: null);
            Assert.Equal(expected: 0, actual: RendersOf(instance: Infinity));
        }

        Assert.DoesNotContain(collection: m_host.Footprints, filter: static footprint => (footprint.Producer == Infinity));
    }
}
