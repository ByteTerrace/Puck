using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// A node leaving the graph hands every render it completed to the runtime (TakeRetiredCompletions), by instance name,
// whether it is disposed at once or held by a kept consumer, across any number of reconfigurations between reads, and
// each render is handed over once.
public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void ARemovedViewsUnreadRenderIsHandedOverWithItsGrid() {
        using var views = new PackageViews(grids: [(PackageView, 0.625d), ("second", 0.5d)]);

        views.Clear();
        views.Frame();
        Assert.True(condition: views.Reconfigure(PackageView));

        // The removed view's render at 0.5 completed before the next read, so the views' renders since the last read
        // name no one grid, though the view that remains rendered at 0.625 alone.
        var retired = views.Runtime.TakeRetiredCompletions();
        var remaining = views.Live();

        Assert.Equal(expected: new ShaderPipelineCompletions(Grid: 0.5d, Renders: 1), actual: retired);
        Assert.Equal(expected: 0.625d, actual: remaining.Grid);
        Assert.Equal(expected: 0d, actual: retired.Then(later: remaining).Grid);
        Assert.Equal(expected: default, actual: views.Runtime.TakeRetiredCompletions());
    }
    [Fact]
    public void ReconfigurationsBetweenReadsLoseNoRetiredRender() {
        using var views = new PackageViews(grids: [(PackageView, 0.625d), ("second", 0.5d), ("third", 0.75d)]);

        views.Clear();
        views.Frame();
        Assert.True(condition: views.Reconfigure(PackageView, "third"));
        views.Frame();
        Assert.True(condition: views.Reconfigure(PackageView));
        // Added again under its name, the second view is a new node, and its renders are handed over when it leaves.
        Assert.True(condition: views.Reconfigure(PackageView, "second"));
        var again = views.Runtime.NodeOf(instance: "second")!;

        TestLiveness.Until(step: () => {
            views.Frame();
            return (again.FrameCounter >= 2UL);
        });
        var renders = (1 + ((int)again.FrameCounter));

        Assert.True(condition: views.Reconfigure(PackageView));

        // The second view's renders at 0.5, both nodes', and the third's two at 0.75.
        Assert.Equal(expected: new ShaderPipelineCompletions(Grid: 0d, Renders: (renders + 2)), actual: views.Runtime.TakeRetiredCompletions());
        Assert.Equal(expected: default, actual: views.Runtime.TakeRetiredCompletions());
    }
    [Fact]
    public void AHeldRetiredNodeHandsEachRenderOverOnce() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        var roots = new[] { new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0) };

        using var runtime = TwoInstances(gpu: gpu, recorders: recorders);
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: roots,
            runtime: runtime
        );

        runtime.AccountFor(instances: ["camera"]);
        frames.Settle();
        var camera = runtime.NodeOf(instance: "camera")!;
        var main = runtime.NodeOf(instance: "main")!;

        _ = camera.TakeCompletions();
        var before = camera.FrameCounter;

        frames.Next(count: 3);
        var rendered = ((int)(camera.FrameCounter - before));

        Assert.True(condition: (rendered > 0));
        // The kept root still samples the removed camera. The camera retires at once, since a graph instance's image binding
        // holds the image by lease rather than the producer, so nothing holds the node and the renders it completed are
        // handed over as it goes.
        Assert.True(condition: runtime.TryReconfigure(
            graphs: [Graph(pipeline: ScreensGraph(pool: false))],
            refusal: out var refusal,
            root: "main",
            set: Set(Instance(name: "main"))
        ), userMessage: refusal?.Message);
        Assert.Equal(expected: 0, actual: runtime.RetiredProducers);

        var after = new Frames(footprints: [], roots: roots, runtime: runtime);

        main.WaitForBuild();
        TestLiveness.Until(step: () => {
            _ = after.Next();
            return (runtime.RetiredProducers == 0);
        });
        after.Next(count: 3);

        Assert.Equal(expected: new ShaderPipelineCompletions(Grid: 1d, Renders: rendered), actual: runtime.TakeRetiredCompletions());
        Assert.Equal(expected: default, actual: runtime.TakeRetiredCompletions());
    }
    [Fact]
    public void AnInstanceNobodyAccountsForKeepsNoRetiredEntry() {
        using var views = new PackageViews(grids: [(PackageView, 0.625d), ("second", 0.5d), ("pane", 0.75d), ("source$a", 1d), ("source$b", 1d)]);

        // Only the second view is read for; the pane and the sources leave over two reconfigurations and keep nothing.
        views.Runtime.AccountFor(instances: ["second"]);
        views.Clear();
        views.Frame();
        Assert.True(condition: views.Reconfigure(PackageView, "second", "source$b"));
        views.Frame();
        Assert.True(condition: views.Reconfigure(PackageView, "second"));
        Assert.Equal(expected: default, actual: views.Runtime.TakeRetiredCompletions());

        // The view read for still hands its renders over when it leaves: one in each of the three frames since the read.
        views.Frame();
        Assert.True(condition: views.Reconfigure(PackageView));
        Assert.Equal(expected: new ShaderPipelineCompletions(Grid: 0.5d, Renders: 3), actual: views.Runtime.TakeRetiredCompletions());

        // A reader that stops reading drops what it has not read.
        views.Runtime.AccountFor(instances: ["third"]);
        Assert.True(condition: views.Reconfigure(PackageView, "third"));
        TestLiveness.Until(step: () => {
            views.Frame();
            return (views.Runtime.NodeOf(instance: "third") is { FrameCounter: > 0UL });
        });
        Assert.True(condition: views.Reconfigure(PackageView));
        views.Runtime.AccountFor(instances: []);
        Assert.Equal(expected: default, actual: views.Runtime.TakeRetiredCompletions());
    }

    // Package views, each a root at its own render grid, on a device that holds fences until a wait or the law completes
    // them.
    private sealed class PackageViews : IDisposable {
        private readonly FakeGpuDevice m_gpu = new(holdFences: true);
        private readonly ViewPackage m_view = new();

        private string[] m_names;
        private long m_index;

        public PackageViews((string Name, double Grid)[] grids) {
            var recorders = new Recorders();

            foreach (var (name, grid) in grids) {
                m_view.Grids[name] = grid;
            }

            m_names = [.. grids.Select(selector: static view => view.Name)];
            recorders.Registry.Register(factory: m_view, package: RenderGraphPackageCatalog.SdfWorld);
            Runtime = RenderGraphRuntimeLawTests.Runtime(m_gpu, recorders, SetOf(names: m_names), PackageView, new RenderGraphRuntimeGraph[m_names.Length]);
            Runtime.AccountFor(instances: m_names);
            TestLiveness.Until(step: () => {
                Frame();
                return m_names.All(predicate: name => (Runtime.NodeOf(instance: name) is { FrameCounter: > 0UL }));
            });
        }

        public RenderGraphRuntime Runtime { get; }

        // Completes every render so far and reads every view's and every retired instance's completions, so the next
        // read holds only what follows.
        public void Clear() {
            CompleteAll();
            Frame();
            _ = Live();
            _ = Runtime.TakeRetiredCompletions();
        }
        public void Dispose() => Runtime.Dispose();
        public void Frame() {
            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: [],
                Index: m_index,
                Roots: [.. m_names.Select(selector: static name => new RenderGraphRoot(Height: 1.0, Instance: name, Width: 1.0))],
                Tick: m_index
            );

            m_index++;
            _ = Runtime.ProduceFrame(context: default, frame: in frame);
        }
        // Completes every render and reads the views' completions since the previous read.
        public ShaderPipelineCompletions Live() {
            CompleteAll();

            var completions = default(ShaderPipelineCompletions);

            foreach (var name in m_names) {
                var node = Runtime.NodeOf(instance: name)!;

                node.PollReadbacks();
                completions = completions.Then(later: node.TakeCompletions());
            }

            return completions;
        }
        public bool Reconfigure(params string[] names) {
            m_names = names;

            return Runtime.TryReconfigure(
                graphs: new RenderGraphRuntimeGraph?[names.Length],
                refusal: out _,
                root: PackageView,
                set: SetOf(names: names)
            );
        }

        private void CompleteAll() {
            foreach (var fence in m_gpu.SubmittedFences) {
                fence.Completed = true;
            }
        }
        private static RenderGraphInstanceSet SetOf(string[] names) => Set([.. names.Select(selector: static name => PackageInstance() with { Name = name })]);
    }
}
