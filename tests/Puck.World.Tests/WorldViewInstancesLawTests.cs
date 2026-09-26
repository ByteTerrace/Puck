using Puck.Abstractions.Counting;
using Puck.Hosting;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the views a world renders beside its own: a declared extent past the display keeps its aspect, a camera reads
/// only the views a screen shows (so its previous-frame reads, and the leases the runtime acquires for them each refresh,
/// scale with the views shown rather than with every view), and a presentation that sets its views as they were allocates
/// nothing and publishes nothing.
/// </summary>
public sealed class WorldViewInstancesLawTests {
    private static WorldView View(string name, WorldViewDemand demand, bool filmsWorld = true) => new(
        Demand: demand,
        FilmsWorld: filmsWorld,
        Height: 0.25,
        Name: name,
        Refresh: RenderGraphRefresh.EveryFrame,
        Width: 0.25
    );

    [Fact]
    public void ADeclaredExtentPastTheDisplayKeepsItsAspect() {
        var (width, height) = WorldViewInstances.Fit(
            displayHeight: 720,
            displayWidth: 1280,
            height: 256,
            width: 2048
        );

        // Scaled by one factor: the full display width, and the height that keeps 8:1.
        Assert.Equal(
            actual: (width, (height * 720.0)),
            expected: (1.0, 160.0)
        );
        Assert.Equal(
            actual: WorldViewInstances.Fit(
                displayHeight: 720,
                displayWidth: 1280,
                height: 72,
                width: 128
            ),
            expected: ((128.0 / 1280.0), (72.0 / 720.0))
        );
    }
    [Fact]
    public void ACameraReadsOnlyTheViewsAScreenShows() {
        const int Cameras = 6;
        var views = WorldViewInstances.Of(views: [
            View(name: "shown", demand: WorldViewDemand.Screen | WorldViewDemand.Root),
            .. Enumerable.Range(count: (Cameras - 1), start: 0).Select(selector: static index => View(
                demand: WorldViewDemand.Root,
                name: $"hud{index}"
            )),
        ]);
        var instances = views.Instances(sources: []);
        var previousFrameReads = instances.Sum(selector: static instance => instance.Reads.Count(predicate: static read => read.PreviousFrame));

        // One view a screen shows: each camera reads it once, so the reads grow with the cameras, never with their square.
        Assert.Equal(
            actual: previousFrameReads,
            expected: Cameras
        );
        Assert.All(
            action: static instance => Assert.Equal(
                actual: instance.Reads.Single().Producer,
                expected: "shown"
            ),
            collection: instances
        );
    }
    [Fact]
    public void ASteadyFrameOfViewsAllocatesNothingAndPublishesNothing() {
        var set = new WorldViewSet();
        var shown = View(name: "shown", demand: WorldViewDemand.Screen);
        var session = View(demand: WorldViewDemand.Screen, filmsWorld: false, name: "session$0");
        var hud = View(name: "hud", demand: WorldViewDemand.Root);

        bool Frame() {
            set.Begin();
            set.Set(view: in session);
            set.Set(view: in shown);
            set.Set(view: in hud);

            return set.TryPublish(instances: out _);
        }

        Assert.True(condition: Frame());
        // Cameras before sessions, each group by name.
        Assert.Equal(
            actual: set.Instances.Views.Select(selector: static view => view.Name),
            expected: ["hud", "shown", "session$0"]
        );
        Assert.False(condition: Frame());
        Assert.Equal(
            actual: AllocationWindow.Least(window: () => _ = Frame()),
            expected: 0L
        );

        // A changed demand and a view the frame no longer sets are each published.
        hud = View(name: "hud", demand: WorldViewDemand.Root | WorldViewDemand.Screen);
        Assert.True(condition: Frame());
        set.Begin();
        set.Set(view: in shown);
        Assert.True(condition: set.TryPublish(instances: out var published));
        Assert.Equal(
            actual: published.Views.Single().Name,
            expected: "shown"
        );
    }
}
