using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for where <see cref="WorldViewGraphHost"/> places the world's views in the synthesized root, the placement
/// <see cref="WorldFramePresenter.PrepareGraph"/> hands it each frame (<see cref="WorldViewGraphHost.PlaceViews"/>):
/// the first view's footprint is always added, since it is the base the root draws over, and a later view's only while
/// it is shown, at its rect at its render scale; a lone view covering the whole display at native scale is never shown,
/// so the root stands for the world; a view the world has not rendered is not shown; and before the world's first frame
/// the first view is placed, not shown, over the whole display so the world is still scheduled.
/// </summary>
public sealed class WorldViewPlacementLawTests : IDisposable {
    private static readonly NormalizedRect Whole = new(Height: 1f, Width: 1f, X: 0f, Y: 0f);
    private static readonly WorldViewDefaults Views = new();

    private readonly string m_directory = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-world-view-placement-{Guid.NewGuid():N}"
    );
    private readonly WorldViewGraphHost m_host;
    private readonly FakeGraphInstances m_instances;
    private readonly IReadOnlyList<string> m_viewPasses;

    public WorldViewPlacementLawTests() {
        Directory.CreateDirectory(path: m_directory);
        m_host = new WorldViewGraphHost(
            documentDirectory: m_directory,
            packager: new ShaderPackager(compiler: new ShaderCompiler(cacheDirectory: Path.Combine(
                path1: m_directory,
                path2: "cache"
            )))
        );
        m_instances = FakeGraphInstances.Attach(
            create: static name => new ShaderPipelineRenderNode(
                deviceContext: new RefusingGpuDevice(),
                height: 4,
                hostsOnDirectX: false,
                name: name,
                width: 4
            ),
            host: m_host
        );
        m_viewPasses = WorldRootGraph.Compose(
            extensions: null,
            overlay: false,
            packages: RenderGraphPackageCatalog.Shipped,
            views: WorldRootGraph.ViewsOf(views: Views)
        ).ViewPasses;
        m_host.BeginFrame(views: Views);
        Assert.True(condition: (m_viewPasses.Count > 1));
    }

    private static SdfViewSnapshot View(NormalizedRect region, float renderScale = 1f) => new(
        Camera: default,
        Region: region
    ) {
        RenderScale = renderScale,
    };
    // The footprints the world's producers add this frame, by producer, as (width, height).
    private Dictionary<string, (double Width, double Height)> WorldFootprints() => m_host.Footprints
        .Where(predicate: static footprint => footprint.Producer.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldViewGraphs.WorldInstance
        ))
        .ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static footprint => (footprint.Width, footprint.Height),
            keySelector: static footprint => footprint.Producer
        );
    private RenderGraphPlacement PlacementOf(int view) {
        Assert.True(condition: m_host.TryGet(
            instance: WorldViewGraphs.MainInstance,
            pass: m_viewPasses[view],
            placement: out var placement
        ));

        return placement;
    }

    public void Dispose() {
        m_host.Dispose();
        m_instances.Dispose();

        try {
            Directory.Delete(
                path: m_directory,
                recursive: true
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }
    }
    [Fact]
    public void TheFirstViewsFootprintIsAlwaysAddedAndALaterViewsOnlyWhileItIsShown() {
        var left = new NormalizedRect(Height: 1f, Width: 0.5f, X: 0f, Y: 0f);
        var right = new NormalizedRect(Height: 1f, Width: 0.5f, X: 0.5f, Y: 0f);

        Assert.True(condition: m_host.PlaceView(region: left, renderScale: 0.5f, sharpness: 0.25f, shown: false, uncovered: false, view: 0));
        Assert.True(condition: m_host.PlaceView(region: right, renderScale: 1f, sharpness: 0.25f, shown: false, uncovered: false, view: 1));
        Assert.Equal(
            actual: WorldFootprints(),
            expected: new Dictionary<string, (double Width, double Height)>(comparer: StringComparer.Ordinal) {
                [WorldRootGraph.ProducerOf(view: 0)] = (0.25, 0.5),
            }
        );
        Assert.False(condition: PlacementOf(view: 1).Shown);

        m_host.BeginFrame(views: Views);
        Assert.True(condition: m_host.PlaceView(region: left, renderScale: 0.5f, sharpness: 0.25f, shown: true, uncovered: false, view: 0));
        Assert.True(condition: m_host.PlaceView(region: right, renderScale: 1f, sharpness: 0.25f, shown: true, uncovered: false, view: 1));
        Assert.Equal(
            actual: WorldFootprints(),
            expected: new Dictionary<string, (double Width, double Height)>(comparer: StringComparer.Ordinal) {
                [WorldRootGraph.ProducerOf(view: 0)] = (0.25, 0.5),
                [WorldRootGraph.ProducerOf(view: 1)] = (0.5, 1.0),
            }
        );
        Assert.Equal(
            actual: PlacementOf(view: 1),
            expected: new RenderGraphPlacement(Height: 1f, Left: 0.5f, Sharpness: 0.25f, Shown: true, Top: 0f, Width: 0.5f)
        );

        // A view the frame does not place is not shown, and one past the root's views is not placed at all.
        Assert.False(condition: PlacementOf(view: 2).Shown);
        Assert.False(condition: m_host.PlaceView(region: Whole, renderScale: 1f, sharpness: 0f, shown: true, uncovered: false, view: m_viewPasses.Count));
    }
    [Fact]
    public void BeforeTheWorldsFirstFrameTheFirstViewIsPlacedHiddenOverTheWholeDisplay() {
        m_host.PlaceViews(
            panesCover: false,
            rendered: static _ => true,
            sharpness: 0.5f,
            views: []
        );

        Assert.False(condition: PlacementOf(view: 0).Shown);
        Assert.Equal(
            actual: WorldFootprints(),
            expected: new Dictionary<string, (double Width, double Height)>(comparer: StringComparer.Ordinal) {
                [WorldRootGraph.ProducerOf(view: 0)] = (1.0, 1.0),
            }
        );
    }
    [Fact]
    public void ALoneWholeDisplayViewAtNativeScaleIsNeverShown() {
        m_host.PlaceViews(
            panesCover: false,
            rendered: static _ => true,
            sharpness: 0f,
            views: [View(region: Whole)]
        );

        Assert.False(condition: PlacementOf(view: 0).Shown);

        // The same view at a reduced render scale, or over part of the display, is shown once rendered.
        m_host.BeginFrame(views: Views);
        m_host.PlaceViews(
            panesCover: false,
            rendered: static _ => true,
            sharpness: 0f,
            views: [View(region: Whole, renderScale: 0.5f)]
        );
        Assert.True(condition: PlacementOf(view: 0).Shown);
        Assert.Equal(
            actual: WorldFootprints()[WorldRootGraph.ProducerOf(view: 0)],
            expected: (0.5, 0.5)
        );

        m_host.BeginFrame(views: Views);
        m_host.PlaceViews(
            panesCover: false,
            rendered: static _ => true,
            sharpness: 0f,
            views: [View(region: new NormalizedRect(Height: 0.8f, Width: 0.8f, X: 0.1f, Y: 0.1f))]
        );
        Assert.True(condition: PlacementOf(view: 0).Shown);
    }
    // The first view's pass owes the letterbox wherever no rect the root shows covers the display, shown or not: a
    // lone whole-display view, a shown whole-display view or a whole-display pane covers it, and anything else leaves
    // part of it uncovered, including a split whose first view has not rendered yet and the frames before the first.
    [Fact]
    public void TheDisplayIsUncoveredUnlessOneShownRectCoversItWhole() {
        var left = new NormalizedRect(Height: 1f, Width: 0.5f, X: 0f, Y: 0f);
        var right = new NormalizedRect(Height: 1f, Width: 0.5f, X: 0.5f, Y: 0f);

        m_host.PlaceViews(
            panesCover: false,
            rendered: static view => (view == 1),
            sharpness: 0f,
            views: [View(region: left), View(region: right)]
        );
        Assert.Equal(
            actual: (PlacementOf(view: 0).Shown, PlacementOf(view: 0).Uncovered),
            expected: (false, true)
        );

        m_host.BeginFrame(views: Views);
        m_host.PlaceViews(
            panesCover: false,
            rendered: static _ => true,
            sharpness: 0f,
            views: [View(region: Whole)]
        );
        Assert.Equal(
            actual: (PlacementOf(view: 0).Shown, PlacementOf(view: 0).Uncovered),
            expected: (false, false)
        );

        m_host.BeginFrame(views: Views);
        m_host.PlaceViews(
            panesCover: false,
            rendered: static _ => true,
            sharpness: 0f,
            views: [View(region: Whole, renderScale: 0.5f)]
        );
        Assert.Equal(
            actual: (PlacementOf(view: 0).Shown, PlacementOf(view: 0).Uncovered),
            expected: (true, false)
        );

        foreach (var panesCover in ((ReadOnlySpan<bool>)[false, true])) {
            m_host.BeginFrame(views: Views);
            m_host.PlaceViews(
                panesCover: panesCover,
                rendered: static _ => true,
                sharpness: 0f,
                views: []
            );
            Assert.Equal(
                actual: PlacementOf(view: 0).Uncovered,
                expected: !panesCover
            );
        }
    }
    [Fact]
    public void AViewTheWorldHasNotRenderedIsNotShown() {
        m_host.PlaceViews(
            panesCover: false,
            rendered: static view => (view == 0),
            sharpness: 0f,
            views: [
                View(region: new NormalizedRect(Height: 1f, Width: 0.5f, X: 0f, Y: 0f)),
                View(region: new NormalizedRect(Height: 1f, Width: 0.5f, X: 0.5f, Y: 0f)),
            ]
        );

        Assert.True(condition: PlacementOf(view: 0).Shown);
        Assert.False(condition: PlacementOf(view: 1).Shown);
        Assert.Equal(
            actual: Assert.Single(collection: WorldFootprints()).Key,
            expected: WorldRootGraph.ProducerOf(view: 0)
        );

        // With no render root attached, no view has an output, so none is shown.
        m_host.BeginFrame(views: Views);
        m_host.PlaceViews(
            panesCover: false,
            rendered: null,
            sharpness: 0f,
            views: [View(region: new NormalizedRect(Height: 1f, Width: 0.5f, X: 0f, Y: 0f))]
        );
        Assert.False(condition: PlacementOf(view: 0).Shown);
    }
}
