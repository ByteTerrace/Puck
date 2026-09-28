using Puck.Hosting;

namespace Puck.Shaders.Tests;

// A runtime whose instance set changes while it runs, and an instance whose graph arrives after it joined the set.
public sealed partial class RenderGraphRuntimeLawTests {
    private static readonly RenderGraphRoot[] CameraAndMain = [
        new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0),
        new RenderGraphRoot(Height: 1.0, Instance: "camera", Width: 1.0),
    ];

    private static RenderGraphRuntime TwoInstances(FakePipelineGpu gpu, Recorders recorders) => Runtime(
        gpu,
        recorders,
        Set(
            Instance(name: "camera"),
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "camera")
            )
        ),
        "main",
        Graph(pipeline: CameraGraph()),
        Graph(ScreensGraph(false, "screen"), ("screen", "camera"))
    );

    [Fact]
    public void AKeptInstanceKeepsItsNodeAndAnAddedOneRendersFromTheNextFrame() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);

        using var runtime = TwoInstances(
            gpu: gpu,
            recorders: recorders
        );

        var frames = new Frames(
            footprints: [],
            roots: CameraAndMain,
            runtime: runtime
        );

        frames.Settle();

        var camera = runtime.NodeOf(instance: "camera");
        var main = runtime.NodeOf(instance: "main");
        var records = recorders.Of(instance: "camera").Records;

        Assert.True(
            condition: runtime.TryReconfigure(
                graphs: [null, null, Graph(pipeline: CameraGraph())],
                refusal: out var refusal,
                root: "main",
                set: Set(
                    Instance(name: "camera"),
                    Instance(
                        name: "main",
                        reads: new RenderGraphRead(Producer: "camera")
                    ),
                    Instance(name: "second")
                )
            ),
            userMessage: refusal?.Message
        );
        Assert.Same(
            actual: runtime.NodeOf(instance: "camera"),
            expected: camera
        );
        Assert.Same(
            actual: runtime.NodeOf(instance: "main"),
            expected: main
        );
        // The kept camera's recorder was never recreated, and it renders on the next frame without a new build.
        Assert.Equal(
            actual: recorders.Of(instance: "camera").Created,
            expected: 1
        );

        var shown = new Frames(
            footprints: [],
            roots: [.. CameraAndMain, new RenderGraphRoot(Height: 0.5, Instance: "second", Width: 0.5)],
            runtime: runtime
        );

        _ = shown.Next();
        Assert.Equal(
            actual: recorders.Of(instance: "camera").Records,
            expected: (records + 1)
        );
        shown.Settle();
        Assert.True(condition: (recorders.Of(instance: "second").Records > 0));
        Assert.Equal(
            actual: recorders.Of(instance: "second").Width,
            expected: ((uint)(Display / 2))
        );
    }
    [Fact]
    public void ARemovedInstanceIsDisposedAndItsCaptureTargetRefuses() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);

        using var runtime = TwoInstances(
            gpu: gpu,
            recorders: recorders
        );

        new Frames(
            footprints: [],
            roots: CameraAndMain,
            runtime: runtime
        ).Settle();

        var target = runtime.CaptureTarget(instance: "main");

        Assert.True(
            condition: runtime.TryReconfigure(
                graphs: [null],
                refusal: out var refusal,
                root: "camera",
                set: Set(Instance(name: "camera"))
            ),
            userMessage: refusal?.Message
        );
        Assert.Null(@object: runtime.NodeOf(instance: "main"));
        Assert.Equal(
            actual: runtime.Root,
            expected: "camera"
        );

        var request = CaptureRequest();

        target.RequestCapture(request: request);
        Assert.NotNull(@object: Outcome(request: request).Error);

        // The camera is the root now, and renders and captures on its own.
        var frames = new Frames(
            footprints: [],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "camera", Width: 1.0)],
            runtime: runtime
        );

        frames.Settle();
        Assert.False(condition: frames.Next().IsEmpty);
    }
    // A kept root presents its installed graph while its replacement builds, and that graph still samples the output of
    // a producer the reconfiguration removed: the removed producer is held, never disposed under the root's frames,
    // until the root installs a graph that no longer reads it, and then the hold is released and it is disposed once.
    [Fact]
    public void ARemovedProducerAKeptRootStillReadsIsHeldUntilTheRootsReplacementInstalls() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        var roots = new[] { new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0) };

        using var runtime = TwoInstances(
            gpu: gpu,
            recorders: recorders
        );

        new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: roots,
            runtime: runtime
        ).Settle();

        var main = runtime.NodeOf(instance: "main")!;

        Assert.True(
            condition: runtime.TryReconfigure(
                graphs: [Graph(pipeline: ScreensGraph(pool: false))],
                refusal: out var refusal,
                root: "main",
                set: Set(Instance(name: "main"))
            ),
            userMessage: refusal?.Message
        );
        Assert.True(condition: main.HasPendingCandidate);

        var frames = new Frames(
            footprints: [],
            roots: roots,
            runtime: runtime
        );
        FakePipelineGpu.Created? sampled = null;

        using (var opener = new PipelineGateOpener()) {
            gpu.PipelineGate = opener.Gate;
            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;

            try {
                // The replacement, which reads no screen, waits in the driver, so each of these frames records the
                // installed graph, which samples the removed camera's last output: that image lives through every one.
                for (var frame = 0; (frame < 3); frame++) {
                    _ = frames.Next();
                    Assert.True(condition: main.HasPendingCandidate);

                    var view = gpu.DescriptorWrites.Last(predicate: static write => (write.Binding == 1)).Handle;

                    sampled = gpu.CreatedObjects.Single(predicate: created => ((created.Handle + 1) == view));
                    Assert.Equal(
                        actual: sampled.DisposeCount,
                        expected: 0
                    );
                    Assert.Equal(
                        actual: runtime.RetiredProducers,
                        expected: 1
                    );
                }
            } finally {
                gpu.Recording = false;
                gpu.PipelineGate = null;
            }
        }

        main.WaitForBuild();
        _ = frames.Next();
        Assert.False(condition: main.HasPendingCandidate);
        Assert.Equal(
            actual: runtime.RetiredProducers,
            expected: 0
        );
        Assert.Equal(
            actual: sampled!.DisposeCount,
            expected: 1
        );
    }
    [Fact]
    public void ARefusedReconfigurationLeavesTheRuntimeAsItWas() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);

        using var runtime = TwoInstances(
            gpu: gpu,
            recorders: recorders
        );

        var main = runtime.NodeOf(instance: "main");

        Assert.False(condition: runtime.TryReconfigure(
            graphs: [null],
            refusal: out var refusal,
            root: "absent",
            set: Set(Instance(name: "camera"))
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RenderGraphRuntimeRefusalCode.Root
        );
        Assert.Same(
            actual: runtime.NodeOf(instance: "main"),
            expected: main
        );
        Assert.Equal(
            actual: runtime.Instances.Instances.Count,
            expected: 2
        );
    }
    [Fact]
    public void AnInstanceWithoutAGraphRendersNothingUntilOneInstalls() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(
                Instance(name: "camera"),
                Instance(
                    name: "main",
                    reads: new RenderGraphRead(Producer: "camera")
                )
            ),
            "main",
            [null!, Graph(ScreensGraph(false, "screen"), ("screen", "camera"))]
        );

        var frames = new Frames(
            footprints: [],
            roots: CameraAndMain,
            runtime: runtime
        );

        frames.Next(count: 4);
        Assert.Equal(
            actual: recorders.Of(instance: "camera").Records,
            expected: 0L
        );
        Assert.NotNull(@object: runtime.UnservedCaptureReasonOf(instance: "main"));

        Assert.True(condition: runtime.TryInstall(
            graph: Graph(pipeline: CameraGraph()),
            instance: "camera",
            refusal: out var refusal
        ), userMessage: refusal?.Message);

        frames.Settle();
        Assert.True(condition: (recorders.Of(instance: "camera").Records > 0));
        Assert.Null(@object: runtime.UnservedCaptureReasonOf(instance: "main"));
    }
    // An instance whose inputs move to another producer while its graph stays is handed its own pipeline with the new
    // inputs: the set's reads follow the move, the node keeps the graph it has installed and builds nothing, and the
    // new producer renders for it from the next frame.
    [Fact]
    public void AGraphKeepingItsPipelineRebindsItsInputsWithoutBuilding() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        var screens = Graph(ScreensGraph(false, "screen"), ("screen", "camera"));

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(
                Instance(name: "camera"),
                Instance(name: "other"),
                Instance(
                    name: "main",
                    reads: new RenderGraphRead(Producer: "camera")
                )
            ),
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(pipeline: CameraGraph()),
            screens
        );
        var roots = new[] { new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0) };

        new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: roots,
            runtime: runtime
        ).Settle();

        var main = runtime.NodeOf(instance: "main")!;
        var plan = main.Plan;

        Assert.True(condition: (recorders.Of(instance: "camera").Records > 0L));
        Assert.Equal(
            actual: recorders.Of(instance: "other").Records,
            expected: 0L
        );
        Assert.True(
            condition: runtime.TryReconfigure(
                graphs: [null, null, (screens with { Inputs = [new RenderGraphRuntimeInput(Producer: "other", Version: "screen")] })],
                refusal: out var refusal,
                root: "main",
                set: Set(
                    Instance(name: "camera"),
                    Instance(name: "other"),
                    Instance(
                        name: "main",
                        reads: new RenderGraphRead(Producer: "other")
                    )
                )
            ),
            userMessage: refusal?.Message
        );
        Assert.Same(
            actual: main.Plan,
            expected: plan
        );
        Assert.False(condition: main.HasPendingCandidate);

        var records = recorders.Of(instance: "camera").Records;

        new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "other", Width: 1.0)],
            roots: roots,
            runtime: runtime
        ).Settle();
        Assert.True(condition: (recorders.Of(instance: "other").Records > 0L));
        Assert.Equal(
            actual: recorders.Of(instance: "camera").Records,
            expected: records
        );
        Assert.Null(@object: runtime.UnservedCaptureReasonOf(instance: "main"));
    }
    [Fact]
    public void AnInstalledGraphIsRefusedWhenItsInputsDoNotResolve() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);

        using var runtime = TwoInstances(
            gpu: gpu,
            recorders: recorders
        );

        Assert.False(condition: runtime.TryInstall(
            graph: Graph(ScreensGraph(false, "screen"), ("elsewhere", "camera")),
            instance: "main",
            refusal: out var refusal
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RenderGraphRuntimeRefusalCode.InputVersion
        );
    }
}
