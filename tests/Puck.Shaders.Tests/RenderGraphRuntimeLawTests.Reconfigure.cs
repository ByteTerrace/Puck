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
