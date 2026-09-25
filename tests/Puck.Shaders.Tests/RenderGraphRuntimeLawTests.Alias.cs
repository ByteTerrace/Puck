using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of a package pass that draws nothing (<see cref="RenderGraphPackageOutcome.DrewNothing"/>): the runtime
/// publishes the version bound to the pass's input in place of its output, with no copy, a capture of the root reads
/// that image in its own layout, a frame that draws again publishes the output, and a pass whose output another pass
/// reads is refused by name when it draws nothing.
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
    private static (RenderGraphRuntime Runtime, Frames Frames, Recorders Recorders) OverScene(FakePipelineGpu gpu, bool reader = false) {
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
            Graph(OverGraph(reader: reader), ("world", "camera"))
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
}
