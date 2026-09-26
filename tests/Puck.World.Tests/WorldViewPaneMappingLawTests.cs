using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the pane mappings <see cref="WorldViewGraphHost.PublishPanes"/> publishes from the placements the root's
/// <c>place</c> passes draw: each shown view and pane publishes a whole-image <see cref="SourceMapping"/> naming its
/// instance by <see cref="RenderGraphInstance.Handle"/>, at the extent the runtime's latest schedule renders it at, in
/// drawing order (views, then panes); the presentation picker and the hit walk over the live instance set read exactly
/// those mappings; a pane that has not rendered, or that the root does not draw, publishes nothing; and a steady frame
/// publishes without allocating. The hover laws (<c>.Hover</c>) hold the presentation destination: the pane the picker
/// answers for the pointer is the hovered pane, the cursor writer outlines exactly its rect, and a steady hovered frame
/// allocates nothing.
/// </summary>
public sealed partial class WorldViewPaneMappingLawTests : IDisposable {
    private const int Display = 64;
    private const string Pane = "pane";

    private static readonly NormalizedRect Left = new(Height: 1f, Width: 0.5f, X: 0f, Y: 0f);
    private static readonly NormalizedRect Right = new(Height: 1f, Width: 0.5f, X: 0.5f, Y: 0f);
    private static readonly NormalizedRect TopLeft = new(Height: 0.5f, Width: 0.5f, X: 0f, Y: 0f);
    private static readonly WorldViewDefaults Views = new(
        Graphs: [new WorldViewGraph(
            Name: Pane,
            Package: RenderGraphPackageCatalog.SdfWorld
        )],
        Layouts: [new WorldViewLayout(
            Name: "pane",
            Slots: [new WorldViewSlot(Instance: Pane)]
        )]
    );
    private readonly string m_directory = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-world-pane-mapping-{Guid.NewGuid():N}"
    );

    private readonly WorldViewGraphHost m_host;
    private readonly FakeGraphInstances m_instances;

    // The runtime alternates two schedules, so the next frame is never scheduled into its own history.
    private readonly RenderGraphSchedule[] m_schedules = new RenderGraphSchedule[2];
    private readonly RenderGraphRoot[] m_roots = [new RenderGraphRoot(Height: 1, Instance: WorldViewGraphs.MainInstance, Width: 1)];
    private readonly List<SdfViewSnapshot> m_views = [];

    private long m_frame;
    private RenderGraphHistory? m_history;

    public WorldViewPaneMappingLawTests() {
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
                pipelines: new GpuPassPipelineCache(),
                deviceContext: new RefusingGpuDevice(),
                height: 4,
                hostsOnDirectX: false,
                name: name,
                width: 4
            ),
            host: m_host
        );
    }

    private static CameraSnapshot Camera(float x) => CameraSnapshot.LookAt(
        fieldOfViewRadians: 1f,
        position: new Vector3(x: x, y: 1f, z: 5f),
        target: new Vector3(x: x, y: 1f, z: 0f),
        viewportHeight: Display,
        viewportWidth: Display
    );
    private static FixedVector2 Point(double x, double y) => new(
        X: FixedQ4816.FromDouble(value: x),
        Y: FixedQ4816.FromDouble(value: y)
    );
    // One host frame as the presenter prepares it, then the runtime's schedule of it, which the next frame publishes
    // from. `pane` places the pane at that rect, or not at all.
    private void Frame(NormalizedRect? pane) {
        Prepare(pane: pane);
        Schedule();
    }
    // The host's half of a frame, as the presenter's PrepareGraph drives it: begin, place, publish.
    private void Prepare(NormalizedRect? pane) {
        m_host.BeginFrame(views: Views);
        m_host.PlaceViews(
            panesCover: false,
            rendered: static _ => true,
            sharpness: 0f,
            views: m_views
        );

        if (pane is { } region) {
            _ = m_host.Place(
                instance: Pane,
                region: region,
                sharpness: 0f
            );
        }

        m_host.PublishPanes(
            displayHeight: Display,
            displayWidth: Display
        );
    }
    // The runtime's half: schedules the frame the host prepared and hands the schedule to the fake as its latest.
    private void Schedule() {
        var set = m_instances.Instances;

        if (
            (m_history is null) ||
            (m_schedules[0] is null) ||
            (m_schedules[0].Next.Count != set.Instances.Count)
        ) {
            m_history = RenderGraphHistory.Empty(set: set);
            m_schedules[0] = new RenderGraphSchedule(set: set);
            m_schedules[1] = new RenderGraphSchedule(set: set);
        }

        var schedule = m_schedules[((int)(m_frame & 1))];

        RenderGraphScheduler.Schedule(
            frame: new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: m_host.Footprints,
                Index: m_frame,
                Roots: m_roots
            ),
            history: m_history,
            schedule: schedule,
            set: set
        );
        m_history = schedule.Next;
        m_instances.Latest = schedule;
        m_frame++;
    }
    private (string Source, long X, long Y)? Picked(double x, double y) => (m_host.Picker.TryPick(
        pick: out var pick,
        point: new Vector2(
            x: ((float)x),
            y: ((float)y)
        )
    )
        ? (pick.Source.Name, pick.Hit.PixelX, pick.Hit.PixelY)
        : null);

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
    // A pane publishes nothing until its instance has rendered at an extent; then it publishes its whole image over the
    // slot's rect, named by the instance's handle, and a display point maps to the source pixel under it.
    [Fact]
    public void APanePublishesItsInstancesWholeImageOverItsRect() {
        Frame(pane: Left);
        Assert.Empty(collection: m_host.Panes);
        Assert.Null(@object: Picked(x: 8.5, y: 40.5));

        Frame(pane: Left);

        var mapping = Assert.Single(collection: m_host.Panes);
        var index = m_instances.Instances.IndexOf(name: Pane);

        Assert.Equal(
            actual: mapping.Source,
            expected: m_instances.Instances.Instances[index].Handle
        );
        Assert.Equal(
            actual: mapping.Placement,
            expected: new SourcePlacement.Pane(Region: Left)
        );
        Assert.Equal(
            actual: (mapping.SourceWidth, mapping.SourceHeight),
            expected: ((Display / 2), Display)
        );
        Assert.Equal(
            actual: (mapping.SourceWidth, mapping.SourceHeight),
            expected: (m_instances.Latest!.Instances[index].Width, m_instances.Latest.Instances[index].Height)
        );
        Assert.Equal(
            actual: Picked(x: 8.5, y: 40.5),
            expected: (Pane, 8L, 40L)
        );
        Assert.Null(@object: Picked(x: 40.5, y: 40.5));

        // The pane pointer reads the same published mapping.
        Assert.True(condition: m_host.TryGetPane(
            instance: Pane,
            mapping: out var pointer
        ));
        Assert.Same(
            actual: pointer,
            expected: mapping
        );
    }
    // Moving the slot moves the mapping: the point that mapped to the pane's pixel now maps to none, and the same pixel
    // sits under the moved rect.
    [Fact]
    public void MovingTheSlotMovesTheMapping() {
        Frame(pane: Left);
        Frame(pane: Right);

        Assert.Null(@object: Picked(x: 8.5, y: 40.5));
        Assert.Equal(
            actual: Picked(x: 40.5, y: 40.5),
            expected: (Pane, 8L, 40L)
        );

        // A frame that does not place the pane publishes nothing for it.
        Frame(pane: null);
        Assert.Empty(collection: m_host.Panes);
        Assert.False(condition: m_host.TryGetPane(
            instance: Pane,
            mapping: out _
        ));
    }
    // Views publish before panes, in view order, so a pane over a view is topmost; a view not shown publishes nothing.
    [Fact]
    public void ViewsPublishBeneathPanesInDrawingOrder() {
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Left));
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 3f), Region: Right));
        Frame(pane: TopLeft);
        Frame(pane: TopLeft);

        Assert.Equal(
            actual: m_host.Panes.Select(selector: static pane => pane.Source.Name),
            expected: [WorldRootGraph.ProducerOf(view: 0), WorldRootGraph.ProducerOf(view: 1), Pane]
        );
        Assert.Equal(
            actual: Picked(x: 8.5, y: 8.5),
            expected: (Pane, 8L, 8L)
        );
        Assert.Equal(
            actual: Picked(x: 8.5, y: 40.5),
            expected: (WorldRootGraph.ProducerOf(view: 0), 8L, 40L)
        );
        Assert.Equal(
            actual: Picked(x: 40.5, y: 40.5),
            expected: (WorldRootGraph.ProducerOf(view: 1), 8L, 40L)
        );

        // A lone view covering the whole display at native scale is not shown, so the root stands for the world and
        // no pane is published for it.
        m_views.Clear();
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)));
        Frame(pane: null);
        Frame(pane: null);
        Assert.Empty(collection: m_host.Panes);
    }
    // The hit walk runs over the live instance set from the published panes: a point on a view continues through the
    // view's camera into its world, and a point on a pane whose instance renders from no camera ends there until the
    // presenter records one.
    [Fact]
    public void TheHitWalkContinuesThroughTheLiveInstanceSet() {
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Left));
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 3f), Region: Right));
        Frame(pane: TopLeft);
        Frame(pane: TopLeft);

        var set = m_instances.Instances;
        var onView = m_host.Walk(point: Point(x: 40.5, y: 40.5))!;

        Assert.Equal(
            actual: (onView.End, onView.Instance, onView.Steps.Count),
            expected: (RenderGraphHitEnd.World, set.IndexOf(name: WorldRootGraph.ProducerOf(view: 1)), 1)
        );
        Assert.Equal(
            actual: (onView.Steps[0].Hit.PixelX, onView.Steps[0].Hit.PixelY),
            expected: (8L, 40L)
        );

        var onPane = m_host.Walk(point: Point(x: 8.5, y: 8.5))!;

        Assert.Equal(
            actual: (onPane.End, onPane.Steps[0].Mapping.Source.Name),
            expected: (RenderGraphHitEnd.NoCamera, Pane)
        );

        m_host.SetCamera(
            camera: Camera(x: 7f),
            instance: Pane
        );
        Assert.Equal(
            actual: m_host.Walk(point: Point(x: 8.5, y: 8.5))!.End,
            expected: RenderGraphHitEnd.World
        );

        // Off every pane the walk reaches no pane at all.
        Frame(pane: null);
        m_views.Clear();
        Frame(pane: null);
        Frame(pane: null);
        Assert.Empty(collection: m_host.Walk(point: Point(x: 8.5, y: 8.5))!.Steps);
    }
    // A walk from a view's pane continues through the view's camera into its world, meets a screen the binder published
    // standing there, and ends on the screen's source instance at the pixel beneath; a view whose camera misses the
    // screen ends on its world.
    [Fact]
    public void TheHitWalkContinuesThroughAScreenIntoItsSource() {
        const int SourceHeight = 144;
        const int SourceWidth = 160;
        var pattern = WorldImageProducerSettings.SourceOf(
            id: WorldImageProducerSettings.TestPatternId,
            settings: new WorldTestPatternSettings(
                Height: SourceHeight,
                Width: SourceWidth
            )
        );
        // A screen facing the first view's camera from 5 units ahead, 2.4 units wide and 1.8 tall.
        var screens = new WorldScreenMappingSet();

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
        screens.Publish(images: new PatternImages(Height: SourceHeight, Width: SourceWidth));
        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Left));
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 3f), Region: Right));
        Frame(pane: null);
        Frame(pane: null);

        var set = m_instances.Instances;
        var walk = m_host.Walk(point: Point(x: 20.5, y: 40.5))!;

        Assert.Equal(
            actual: (walk.End, walk.Instance, walk.Steps.Count),
            expected: (RenderGraphHitEnd.Producer, set.IndexOf(name: WorldRootGraph.ProducerOf(view: 0)), 2)
        );
        Assert.Equal(
            actual: walk.Steps[1].Mapping,
            expected: screens.Mappings[0]
        );
        Assert.Equal(
            actual: walk.Steps[1].Mapping.Source,
            expected: WorldSourceInstances.Of(shown: [pattern]).HandleOf(screen: 0)
        );

        // The pane's point is the view image's (20.5 / 32, 40.5 / 64); its pinhole ray from (0, 1, 5) meets the face 5
        // units ahead, and the glass's bezel insets the image inside the face.
        var tangent = Math.Tan(a: 0.5);
        var u = (0.5 + ((5.0 * (((2.0 * (20.5 / 32.0)) - 1.0) * tangent)) / 2.4));
        var v = (0.5 - ((5.0 * ((1.0 - (2.0 * (40.5 / 64.0))) * tangent)) / 1.8));
        const double Inner = (1.0 - (2.0 * WorldScreenMappings.Bezel));

        Assert.Equal(
            actual: (walk.Steps[1].Hit.PixelX, walk.Steps[1].Hit.PixelY),
            expected: (((long)Math.Floor(d: (((u - WorldScreenMappings.Bezel) / Inner) * SourceWidth))), ((long)Math.Floor(d: (((v - WorldScreenMappings.Bezel) / Inner) * SourceHeight))))
        );
        Assert.Equal(
            actual: (walk.Steps[1].Hit.PixelX, walk.Steps[1].Hit.PixelY),
            expected: (134L, 133L)
        );

        // The second view's camera, three units to the side, misses the screen.
        var beside = m_host.Walk(point: Point(x: 52.5, y: 40.5))!;

        Assert.Equal(
            actual: (beside.End, beside.Instance, beside.Steps.Count),
            expected: (RenderGraphHitEnd.World, set.IndexOf(name: WorldRootGraph.ProducerOf(view: 1)), 1)
        );

        // With no screens reported, the first view's walk ends on its world too.
        m_host.Screens = null;
        Assert.Equal(
            actual: m_host.Walk(point: Point(x: 20.5, y: 40.5))!.End,
            expected: RenderGraphHitEnd.World
        );
    }
    // A frame whose placements, extents and instances hold publishes the mappings it published before, and the whole
    // host frame (begin, place, publish) allocates nothing.
    [Fact]
    public void ASteadyFramePublishesWithoutAllocating() {
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Left));
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 3f), Region: Right));

        for (var frame = 0; (frame < 4); frame++) {
            Frame(pane: TopLeft);
        }

        var published = m_host.Panes.ToArray();
        var before = GC.GetAllocatedBytesForCurrentThread();

        Prepare(pane: TopLeft);

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(
            actual: allocated,
            expected: 0L
        );
        Assert.Equal(
            actual: m_host.Panes.Count,
            expected: published.Length
        );

        for (var index = 0; (index < published.Length); index++) {
            Assert.Same(
                actual: m_host.Panes[index],
                expected: published[index]
            );
        }
    }

    // Every screen shows its row's source at one known extent.
    private sealed record PatternImages(int Width, int Height) : IWorldScreenImages {
        public bool ShowsRow(int screen) => true;
        public bool TryExtent(int screen, out int width, out int height) {
            (width, height) = (Width, Height);

            return true;
        }
    }
}
