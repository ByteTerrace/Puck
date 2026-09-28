using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of a package pass that draws nothing (<see cref="RenderGraphPackageOutcome.DrewNothing"/>): the runtime
/// publishes the version bound to the pass's input in place of its output, with no copy, a capture of the root reads
/// that image in its own layout (a host's, or an owned input's planned one), a frame that draws again publishes the
/// output, a drawing pass is handed its input shader-readable and its target in render-target layout, every image
/// barrier starts from the layout the last one left, and a pass whose output another pass reads, or would stand for a
/// previous frame's input or for an input a later pass overwrites, is refused by name when it draws nothing.
/// </summary>
public sealed partial class RenderGraphRuntimeLawTests {
    // The root's graph: the world it reads, and one package pass drawn over it into the published image, or, with a
    // reader, a compute pass that reads the drawn image into the published one.
    private static CompiledShaderPipeline OverGraph(bool reader) {
        var passes = (reader
            ? new[] {
                new ShaderPipelinePass(
                    EntryPoint: "main",
                    Inputs: [new ResourceReference(
                        Name: "composed"
                    )],
                    Kind: ShaderPipelineDocumentPassKind.Compute,
                    Name: "after",
                    Outputs: [new ResourceReference(
                        Name: "image"
                    )],
                    Source: "after.hlsl"
                ),
            }
            : null);

        return Compile(definition: new RenderGraphDefinition(
            Name: "over",
            Outputs: [(reader ? "image" : "composed")],
            Packages: [new RenderGraphPackagePass(
                Inputs: ["world"],
                Name: "over",
                Outputs: ["composed"],
                Package: Over
            )],
            Passes: passes,
            Resources: [
                Image(
                    external: true,
                    name: "world"
                ),
                Image(name: "composed"),
                .. (reader
                    ? new[] { Image(name: "image") }
                    : []),
            ],
            Schema: RenderGraphSchemas.Graph
        ));
    }
    // The root's graph with an owned input: a compute pass shades the world it reads into an image of its own, and the
    // package pass draws over that image into the published one.
    private static CompiledShaderPipeline OwnedOverGraph() => Compile(definition: new RenderGraphDefinition(
        Name: "owned-over",
        Outputs: ["composed"],
        Packages: [new RenderGraphPackagePass(
            Inputs: ["lit"],
            Name: "over",
            Outputs: ["composed"],
            Package: Over
        )],
        Passes: [new ShaderPipelinePass(
            EntryPoint: "main",
            Inputs: [new ResourceReference(
                Name: "world"
            )],
            Kind: ShaderPipelineDocumentPassKind.Compute,
            Name: "shade",
            Outputs: [new ResourceReference(
                Name: "lit"
            )],
            Source: "shade.hlsl"
        )],
        Resources: [
            Image(
                external: true,
                name: "world"
            ),
            Image(name: "lit"),
            Image(name: "composed"),
        ],
        Schema: RenderGraphSchemas.Graph
    ));
    private static (RenderGraphRuntime Runtime, Frames Frames, Recorders Recorders) OverScene(FakePipelineGpu gpu, bool reader = false, CompiledShaderPipeline? root = null) {
        var recorders = new Recorders(Camera, Over);
        var set = Set(
            Instance(name: "camera"),
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "camera")
            )
        );
        var runtime = Runtime(
            gpu,
            recorders,
            set,
            "main",
            Graph(pipeline: CameraGraph()),
            Graph((root ?? OverGraph(reader: reader)), ("world", "camera"))
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        );

        return (runtime, frames, recorders);
    }

    [Fact]
    public void AFrameThatDrawsNothingPublishesTheInputVersionAndADrawnFramePublishesTheOutput() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, recorders) = OverScene(gpu: gpu);

        using (runtime) {
            frames.Settle();

            var over = recorders.Of(instance: "main");

            over.Outcome = RenderGraphPackageOutcome.DrewNothing;

            var aliased = frames.Next();

            Assert.NotEqual(
                actual: over.InputImage,
                expected: 0
            );
            Assert.Equal(
                expected: over.InputImage,
                actual: aliased.ImageHandle
            );
            Assert.Equal(
                expected: GpuImageLayout.ShaderReadOnly,
                actual: runtime.Node(instance: 1).PublishedLayout
            );

            over.Outcome = RenderGraphPackageOutcome.Drew;

            var drawn = frames.Next();

            // A drawing package's ports: the sampled input arrives shader-readable and the target in render-target
            // layout, both left so by the planned barriers.
            Assert.Equal(
                actual: (over.InputLayout, over.OutputLayout),
                expected: (GpuImageLayout.ShaderReadOnly, GpuImageLayout.RenderTarget)
            );
            Assert.Equal(
                expected: over.OutputImage,
                actual: drawn.ImageHandle
            );
            Assert.NotEqual(
                expected: over.InputImage,
                actual: drawn.ImageHandle
            );
        }
    }
    [Fact]
    public void ARootCaptureOfAFrameThatDrewNothingReadsTheInputVersionInItsLayout() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, recorders) = OverScene(gpu: gpu);

        using (runtime) {
            frames.Settle();

            var over = recorders.Of(instance: "main");
            var request = CaptureRequest();

            over.Outcome = RenderGraphPackageOutcome.DrewNothing;
            runtime.RequestCapture(request: request);

            var shown = frames.Next();

            Assert.Null(@object: Outcome(request: request).Error);
            Assert.Equal(
                expected: (over.InputImage, GpuImageLayout.ShaderReadOnly),
                actual: Assert.Single(collection: gpu.Readbacks)
            );
            Assert.Equal(
                expected: over.InputImage,
                actual: shown.ImageHandle
            );
        }
    }
    [Fact]
    public void AnOwnedInputStandingForTheOutputIsPublishedAndCapturedInItsPlannedLayout() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var root = OwnedOverGraph();
        var lit = Assert.Single(
            collection: root.Plan.Storages,
            predicate: static storage => storage.Versions.Contains(value: "lit")
        );

        // The package samples the shaded image in the fragment stage, so the frame leaves it shader-readable.
        Assert.Equal(
            expected: GpuImageLayout.ShaderReadOnly,
            actual: lit.FrameEnd.Layout
        );

        var (runtime, frames, recorders) = OverScene(
            gpu: gpu,
            root: root
        );

        using (runtime) {
            frames.Settle();

            var over = recorders.Of(instance: "main");
            var request = CaptureRequest();

            over.Outcome = RenderGraphPackageOutcome.DrewNothing;
            runtime.RequestCapture(request: request);

            var shown = frames.Next();

            Assert.Null(@object: Outcome(request: request).Error);
            Assert.Equal(
                expected: over.InputImage,
                actual: shown.ImageHandle
            );
            Assert.Equal(
                expected: lit.FrameEnd.Layout,
                actual: runtime.Node(instance: 1).PublishedLayout
            );
            Assert.Equal(
                expected: (over.InputImage, lit.FrameEnd.Layout),
                actual: Assert.Single(collection: gpu.Readbacks)
            );
        }
    }
    [Fact]
    public void APassWhoseOutputAnotherPassReadsIsRefusedByNameWhenItDrawsNothing() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, recorders) = OverScene(
            gpu: gpu,
            reader: true
        );

        using (runtime) {
            frames.Settle();
            recorders.Of(instance: "main").Outcome = RenderGraphPackageOutcome.DrewNothing;

            var refusal = Assert.Throws<InvalidOperationException>(testCode: () => frames.Next());

            Assert.Contains(
                expectedSubstring: "Package pass 'over' drew nothing, but its output 'composed' is read by another pass",
                actualString: refusal.Message
            );
        }
    }
    /// <summary>Over three frames that draw and three that draw nothing, every image barrier starts from the layout the
    /// image's last barrier left it in, whether the frame publishes the output or the input standing for it.</summary>
    [Fact]
    public void EveryImageTransitionStartsFromTheLayoutTheLastOneLeft() {
        foreach (var root in ((CompiledShaderPipeline[])[OverGraph(reader: false), OwnedOverGraph()])) {
            var gpu = new FakePipelineGpu();

            var (runtime, frames, recorders) = OverScene(
                gpu: gpu,
                root: root
            );

            using (runtime) {
                frames.Settle();
                gpu.Recording = true;

                var over = recorders.Of(instance: "main");

                foreach (var outcome in ((RenderGraphPackageOutcome[])[RenderGraphPackageOutcome.Drew, RenderGraphPackageOutcome.DrewNothing])) {
                    over.Outcome = outcome;

                    for (var frame = 0; (frame < 3); frame++) {
                        _ = frames.Next();
                    }
                }

                AssertTransitionsContinue(gpu: gpu);
            }
        }
    }
    /// <summary>A history image read at its previous frame rests in the layout that frame's role left it in, which the
    /// current frame's plan does not describe, so an output cannot stand for it: the pass is refused by name when it
    /// draws nothing, before any frame presents the image from a layout it is not in.</summary>
    [Fact]
    public void AnOutputCannotStandForAPreviousFrameInputAndIsRefusedByName() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, recorders) = OverScene(
            gpu: gpu,
            root: HistoryOverGraph()
        );

        using (runtime) {
            frames.Settle();
            gpu.Recording = true;
            recorders.Of(instance: "main").Outcome = RenderGraphPackageOutcome.DrewNothing;

            var refusal = Record.Exception(testCode: () => {
                for (var frame = 0; (frame < 3); frame++) {
                    _ = frames.Next();
                }
            });

            AssertTransitionsContinue(gpu: gpu);
            Assert.Contains(
                expectedSubstring: "Package pass 'over' drew nothing, but its output 'composed' would stand for the previous frame of 'acc'",
                actualString: Assert.IsType<InvalidOperationException>(@object: refusal).Message
            );
        }
    }
    /// <summary>An input a later pass overwrites, by writing the version that forwards it, holds that pass's contents
    /// once the frame ends, so an output standing for it would publish pixels the package never read: the pass is
    /// refused by name when it draws nothing.</summary>
    [Fact]
    public void AnOutputCannotStandForAnInputALaterPassOverwritesAndIsRefusedByName() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, recorders) = OverScene(
            gpu: gpu,
            root: ForwardedOverGraph()
        );

        using (runtime) {
            frames.Settle();
            recorders.Of(instance: "main").Outcome = RenderGraphPackageOutcome.DrewNothing;

            var refusal = Assert.Throws<InvalidOperationException>(testCode: () => frames.Next());

            Assert.Contains(
                expectedSubstring: "Package pass 'over' drew nothing, but its output 'composed' would stand for 'lit', which pass 'tone' overwrites later in the frame",
                actualString: refusal.Message
            );
        }
    }

    // The root's graph with an owned input a later pass forwards: a compute pass shades the world into an image, the
    // package pass draws over that image, and a later compute pass writes the version forwarding it, which the graph
    // also publishes.
    private static CompiledShaderPipeline ForwardedOverGraph() => Compile(definition: new RenderGraphDefinition(
        Name: "forwarded-over",
        Outputs: ["composed", "toned"],
        Packages: [new RenderGraphPackagePass(
            Inputs: ["lit"],
            Name: "over",
            Outputs: ["composed"],
            Package: Over
        )],
        Passes: [
            new ShaderPipelinePass(
                EntryPoint: "main",
                Inputs: [new ResourceReference(Name: "world")],
                Kind: ShaderPipelineDocumentPassKind.Compute,
                Name: "shade",
                Outputs: [new ResourceReference(Name: "lit")],
                Source: "shade.hlsl"
            ),
            new ShaderPipelinePass(
                EntryPoint: "main",
                Inputs: [new ResourceReference(Name: "world")],
                Kind: ShaderPipelineDocumentPassKind.Compute,
                Name: "tone",
                Outputs: [new ResourceReference(Name: "toned")],
                Source: "tone.hlsl"
            ),
        ],
        Resources: [
            Image(
                external: true,
                name: "world"
            ),
            Image(name: "lit"),
            (Image(name: "toned") with { From = "lit" }),
            Image(name: "composed"),
        ],
        Schema: RenderGraphSchemas.Graph
    ));
    // The root's graph with a history input read at its previous frame: a compute pass accumulates the world into a
    // history image, and the package pass draws over that image's previous frame into the published one.
    private static CompiledShaderPipeline HistoryOverGraph() => Compile(definition: new RenderGraphDefinition(
        Name: "history-over",
        Outputs: ["composed"],
        Packages: [new RenderGraphPackagePass(
            Inputs: [new ResourceReference(
                Name: "acc",
                PreviousFrame: true
            )],
            Name: "over",
            Outputs: ["composed"],
            Package: Over
        )],
        Passes: [new ShaderPipelinePass(
            EntryPoint: "main",
            Inputs: [new ResourceReference(Name: "world")],
            Kind: ShaderPipelineDocumentPassKind.Compute,
            Name: "accumulate",
            Outputs: [new ResourceReference(Name: "acc")],
            Source: "accumulate.hlsl"
        )],
        Resources: [
            Image(
                external: true,
                name: "world"
            ),
            Image(
                history: true,
                name: "acc"
            ),
            Image(name: "composed"),
        ],
        Schema: RenderGraphSchemas.Graph
    ));
    // Every recorded image barrier that states an old layout states the one the image's previous barrier left it in; a
    // barrier from Undefined discards the contents and may follow any layout.
    private static void AssertTransitionsContinue(FakePipelineGpu gpu) {
        var last = new Dictionary<nint, GpuImageLayout>();
        var broken = new List<string>();

        foreach (var (barrier, handle) in gpu.Barriers) {
            if (barrier.Kind != ShaderPipelineBarrierKind.Image) {
                continue;
            }
            if (
                (barrier.OldLayout != GpuImageLayout.Undefined) &&
                last.TryGetValue(key: handle, value: out var left) &&
                (left != barrier.OldLayout)
            ) {
                broken.Add(item: $"image {handle}: {barrier.OldLayout} -> {barrier.NewLayout} after it was left {left}");
            }

            last[handle] = barrier.NewLayout;
        }

        Assert.Empty(collection: broken);
    }
}
