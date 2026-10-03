using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldViewPaneMappingLawTests {
    private const int ScreenSourceHeight = 144;
    private const int ScreenSourceWidth = 160;

    // A lone view covering the whole display is no pane, tonemapped or not, so nothing hovers or outlines it; yet the
    // display is that view, so a display point walks from it through its camera, meets the screen standing ahead and
    // ends on the screen's source.
    [InlineData(null)]
    [InlineData(WorldTonemap.Filmic)]
    [Theory]
    public void AWalkFromALoneWholeDisplayViewContinuesThroughAScreenIntoItsSource(WorldTonemap? tonemap) {
        var (screens, pattern) = ScreenAhead();

        m_host.Screens = screens;
        m_tonemap = tonemap;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)));
        Frame(pane: null);
        Frame(pane: null);

        var set = m_instances.Instances;
        var walk = m_host.Walk(point: Point(x: 32.5, y: 32.5))!;

        Assert.Empty(collection: m_host.Panes);
        Assert.Null(@object: Picked(x: 32.5, y: 32.5));
        Assert.Null(@object: m_host.Hover(point: new Vector2(x: 32.5f, y: 32.5f)));
        Assert.Equal(
            actual: (walk.End, walk.Instance, walk.Steps.Count),
            expected: (RenderGraphHitEnd.Producer, set.IndexOf(name: WorldRootGraph.ProducerOf(view: 0)), 2)
        );
        Assert.Equal(
            actual: (walk.Steps[0].Instance, walk.Steps[0].Placement, walk.Steps[0].Mapping.Source.Name, walk.Steps[0].Hit.PixelX, walk.Steps[0].Hit.PixelY),
            expected: (-1, -1, WorldRootGraph.ProducerOf(view: 0), 32L, 32L)
        );
        Assert.Equal(
            actual: walk.Steps[1].Mapping.Source,
            expected: WorldSourceInstances.Of(shown: [pattern], world: WorldDefinitionLoader.BootInstanceName).HandleOf(screen: 0)
        );

        // A pane over the view is topmost where it stands, and the view is beneath it everywhere else.
        m_tonemap = null;
        Frame(pane: TopLeft);
        Frame(pane: TopLeft);
        Assert.Equal(
            actual: m_host.Walk(point: Point(x: 8.5, y: 8.5))!.Steps[0].Mapping.Source.Name,
            expected: Pane
        );
        Assert.Equal(
            actual: m_host.Walk(point: Point(x: 32.5, y: 32.5))!.End,
            expected: RenderGraphHitEnd.Producer
        );

        // A view that no longer covers the whole display is a pane, and nothing lies beneath the panes.
        m_views[0] = m_views[0] with { Region = Left };
        Frame(pane: null);
        Frame(pane: null);
        Assert.Null(@object: m_host.DisplayView);
        Assert.Empty(collection: m_host.Walk(point: Point(x: 48.5, y: 32.5))!.Steps);
    }

    // A screen facing a camera at x = 0 from five units ahead, 2.4 units wide and 1.8 tall, showing a test pattern.
    private static (WorldScreenMappingSet Screens, WorldScreenSource Pattern) ScreenAhead() {
        var pattern = WorldImageProducerSettings.SourceOf(
            id: WorldImageProducerSettings.TestPatternId,
            settings: new WorldTestPatternSettings(
                Height: ScreenSourceHeight,
                Width: ScreenSourceWidth
            )
        );
        var screens = new WorldScreenMappingSet(world: WorldDefinitionLoader.BootInstanceName);

        screens.Reconcile(
            cameras: [],
            screens: [new WorldScreen(
                HalfDepth: 0.1f,
                HalfHeight: 0.9f,
                HalfWidth: 1.2f,
                Index: 0,
                Origin: new Puck.Assets.Documents.DocumentVector3(value: new Vector3(x: 0f, y: 1f, z: 0f)),
                Right: new Puck.Assets.Documents.DocumentVector3(value: Vector3.UnitX),
                Round: 0f,
                Route: WorldScreenRoute.Passive,
                Source: pattern,
                Up: new Puck.Assets.Documents.DocumentVector3(value: Vector3.UnitY)
            )]
        );
        screens.Publish(images: new PatternImages(Height: ScreenSourceHeight, Width: ScreenSourceWidth));

        return (screens, pattern);
    }
}
