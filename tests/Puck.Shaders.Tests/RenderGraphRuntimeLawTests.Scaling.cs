using Puck.Abstractions.Counting;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// Hub-scale steady-frame behavior: ordinal side tables and sparse active-subset passes keep a hub of a dozen worlds,
// four split-screen panes and parked cameras at zero steady-frame allocation, ask each read package view once per
// frame (never once per pass and never for unread views), preserve withdrawal propagation, and wake on direct node
// captures without scanning every node on steady frames.
public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void HubScaleSteadyFrameAllocatesNothingAndAsksOnlyReadViewsOnce() {
        const int Worlds = 12;
        const int Panes = 4;
        const int ParkedCameras = 12;
        const string DisplayRoot = "display";

        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        var view = new ViewPackage();

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);

        var instances = new List<RenderGraphInstance>(capacity: (Worlds + ParkedCameras + Panes + 1));
        var graphs = new List<RenderGraphRuntimeGraph?>(capacity: (Worlds + ParkedCameras + Panes + 1));
        var cameraGraph = CameraGraph();

        for (var world = 0; (world < Worlds); world++) {
            instances.Add(item: PackageInstance() with { Name = $"world-{world}" });
            graphs.Add(item: null);
        }
        for (var camera = 0; (camera < ParkedCameras); camera++) {
            instances.Add(item: Instance(name: $"cam-{camera}"));
            graphs.Add(item: Graph(pipeline: cameraGraph));
        }
        for (var pane = 0; (pane < Panes); pane++) {
            var target = $"world-{pane}";

            instances.Add(item: Instance(name: $"pane-{pane}", reads: new RenderGraphRead(Producer: target)));
            graphs.Add(item: Graph(pipeline: ScreensGraph(pool: false, "screen"), ("screen", target)));
        }

        var paneNames = Enumerable.Range(start: 0, count: Panes).Select(selector: static pane => $"pane-{pane}").ToArray();
        var panePorts = Enumerable.Range(start: 0, count: Panes).Select(selector: static pane => $"p{pane}").ToArray();
        var rootInputs = Enumerable.Range(start: 0, count: Panes).Select(selector: pane => (panePorts[pane], paneNames[pane])).ToArray();

        instances.Add(item: Instance(
            name: DisplayRoot,
            reads: [.. paneNames.Select(selector: static name => new RenderGraphRead(Producer: name))]
        ));
        graphs.Add(item: Graph(pipeline: ScreensGraph(pool: false, panePorts), rootInputs));

        var set = Set([.. instances]);
        using var runtime = Runtime(gpu, recorders, set, DisplayRoot, graphs.ToArray()!);

        var named = new string[Worlds + ParkedCameras];

        for (var world = 0; (world < Worlds); world++) { named[world] = $"world-{world}"; }
        for (var camera = 0; (camera < ParkedCameras); camera++) { named[Worlds + camera] = $"cam-{camera}"; }

        var roots = new RenderGraphRoot[] { new(Height: 1.0, Instance: DisplayRoot, Width: 1.0) };
        var footprints = new RenderGraphFootprint[Panes * 2];

        for (var pane = 0; (pane < Panes); pane++) {
            footprints[pane] = new RenderGraphFootprint(Consumer: $"pane-{pane}", Height: 1.0, Producer: $"world-{pane}", Width: 1.0);
            footprints[Panes + pane] = new RenderGraphFootprint(Consumer: DisplayRoot, Height: 0.5, Producer: $"pane-{pane}", Width: 0.5);
        }

        var frameIndex = 0L;

        void Produce() {
            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: footprints,
                Index: frameIndex,
                Named: named,
                Roots: roots,
                Tick: frameIndex
            );

            frameIndex++;
            _ = runtime.ProduceFrame(context: default, frame: in frame);
        }

        // Warm up until all four active worlds, four panes and the display root have installed and rendered.
        var displayIndex = set.IndexOf(name: DisplayRoot);

        TestLiveness.Until(step: () => {
            Produce();
            if ((runtime.Nodes[displayIndex]?.FrameCounter ?? 0UL) == 0UL) { return false; }
            for (var pane = 0; (pane < Panes); pane++) {
                if ((runtime.Nodes[pane]?.FrameCounter ?? 0UL) == 0UL) { return false; }
                if ((runtime.Nodes[set.IndexOf(name: $"pane-{pane}")]?.FrameCounter ?? 0UL) == 0UL) { return false; }
            }
            return true;
        });

        // Standing steady state: the 4 active sdf.world views stand unchanged, while the 8 parked worlds and 12 parked
        // cameras remain unread. Each active view is asked IsUnchanged exactly once per frame (never once per pass, and
        // never for parked worlds), and the frame allocates nothing.
        view.Unchanged = true;
        for (var warmup = 0; (warmup < 4); warmup++) { Produce(); }

        var askedBefore = view.AskedUnread.Count;
        var partsBefore = view.Parts.Count;

        Produce();
        Assert.Equal(expected: Panes, actual: (view.AskedUnread.Count - askedBefore));
        Assert.Equal(expected: partsBefore, actual: view.Parts.Count);
        Assert.Equal(expected: ((Worlds - Panes) + ParkedCameras), actual: runtime.Latest!.Unread.Count);
        Assert.Empty(collection: runtime.Latest!.Unnamed);

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Produce));

        // Withdrawal semantics remain intact while the package stands unchanged: withdrawing world-0 forces world-0 to
        // render again on the next frame.
        var world0Before = runtime.Nodes[0]!.FrameCounter;

        runtime.Withdraw(instance: "world-0");
        Produce();
        Assert.True(condition: (runtime.Nodes[0]!.FrameCounter > world0Before));
    }

    [Fact]
    public void DirectNodeCaptureWakesStandingViewWithoutFullScan() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders();
        var view = new ViewPackage();

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance()), PackageView, new RenderGraphRuntimeGraph[1]);
        var index = 0L;

        TestLiveness.Until(step: () => {
            ProducePackageFrame(frameIndex: index++, runtime: runtime);
            return (view.Parts.Count >= SdfWorldPackage.NativeFragment.Passes.Count);
        });

        view.Unchanged = true;
        var recorded = view.Parts.Count;

        ProducePackageFrame(frameIndex: index++, runtime: runtime);
        Assert.Equal(expected: recorded, actual: view.Parts.Count);
        Assert.Null(@object: runtime.SettledCapturePath);

        // Arming directly on the render node (as WorldViewGraphHost.Entry.RequestCapture does) wakes the runtime's
        // O(1) capture check and renders the standing view to serve the capture.
        var request = CaptureRequest();

        runtime.Nodes[0]!.RequestCapture(request: request);
        Assert.Equal(expected: request.Path, actual: runtime.SettledCapturePath);
        ProducePackageFrame(frameIndex: index++, runtime: runtime);
        Assert.Equal(expected: (recorded + SdfWorldPackage.NativeFragment.Passes.Count), actual: view.Parts.Count);
        // The package's HDR output builds a SurfaceEncoder asynchronously on its first capture.
        TestLiveness.Until(step: () => {
            if (request.Completion.IsCompleted) { return true; }
            ProducePackageFrame(frameIndex: index++, runtime: runtime);
            return request.Completion.IsCompleted;
        });
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Null(@object: runtime.SettledCapturePath);
    }
}
