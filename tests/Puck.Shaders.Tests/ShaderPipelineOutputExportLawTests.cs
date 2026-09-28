using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for an exported output (<see cref="IShaderPipelineOutputExport"/>): a node given an export renders its default
/// output into images of its own, one per frame slot, at the export's extent whatever extent it is asked for, and publishes
/// them in its output layout like any output; it copies each written frame into the one image the export creates, which it
/// takes back from its reader before the submission that copies and completes after, and which it never publishes or
/// writes as a render target. A frame the reader still holds the image renders and copies nothing into it.
/// </summary>
public sealed class ShaderPipelineOutputExportLawTests {
    private const uint ExportHeight = 24;
    private const uint ExportWidth = 40;
    private const string Paint = "test.paint";

    private static RenderGraphPackageCatalog Catalog { get; } = new(packages: [
        new RenderGraphPackage(
            Id: Paint,
            Inputs: [],
            Members: [],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Summary: "Writes its output."
        ),
    ]);

    [Fact]
    public void AnExportedOutputRendersIntoTheGraphsOwnImagesAndCopiesIntoTheExport() {
        var gpu = new FakePipelineGpu();
        var painter = new Painter();
        var export = new FakeOutputExport(
            gpu: gpu,
            height: ExportHeight,
            width: ExportWidth
        );
        using var node = Node(
            gpu: gpu,
            painter: painter
        );

        node.Export = export;
        node.Resize(
            height: 480,
            width: 640
        );

        Assert.True(condition: Install(node: node), userMessage: node.LastSwapError?.Message);
        Assert.Equal(expected: (ExportWidth, ExportHeight), actual: node.Extent);

        var image = Assert.Single(collection: export.Created);

        Assert.Same(expected: image, actual: node.ExportedImage);
        Assert.Equal(expected: [ShaderPipelineRenderNode.ExportCopyPass], actual: node.PassLabels[^1..].ToArray());

        // Each frame renders into this slot's own image, never the one the frame before published, publishes it, and
        // copies it into the export.
        var previous = ((nint)0);

        for (var frame = 0; (frame < 4); frame++) {
            painter.Written.Clear();
            gpu.CopiedImages.Clear();

            var surface = node.ProduceFrame(context: default);
            var target = Assert.Single(collection: painter.Written);

            Assert.NotEqual(expected: image.ImageHandle, actual: target);
            Assert.NotEqual(actual: target, expected: previous);
            Assert.Equal(expected: target, actual: surface.ImageHandle);
            Assert.Equal(expected: (target, image.ImageHandle), actual: Assert.Single(collection: gpu.CopiedImages));
            Assert.Equal(expected: GpuImageLayout.ShaderReadOnly, actual: node.PublishedLayout);
            previous = target;
        }

        Assert.Equal(expected: image.Writes, actual: image.Begun);
        Assert.Equal(expected: image.Writes, actual: export.Ended.Count);
        Assert.All(action: static ended => Assert.True(condition: ended.Written), collection: export.Ended);

        // The reader still holds the image: the node renders and publishes its own image, and neither copies nor begins a
        // write.
        var ended = export.Ended.Count;
        var begun = image.Begun;

        export.Released = false;
        painter.Written.Clear();
        gpu.CopiedImages.Clear();

        var held = node.ProduceFrame(context: default);

        Assert.Equal(expected: Assert.Single(collection: painter.Written), actual: held.ImageHandle);
        Assert.Empty(collection: gpu.CopiedImages);
        Assert.Equal(expected: ended, actual: export.Ended.Count);
        Assert.Equal(expected: begun, actual: image.Begun);
    }
    [Fact]
    public void TheExportsImageMovesOnlyThroughTheCopysLayoutsAndRestsInTheHandoffLayout() {
        var gpu = new FakePipelineGpu();
        var export = new FakeOutputExport(
            gpu: gpu,
            height: ExportHeight,
            width: ExportWidth
        );
        using var node = Node(
            gpu: gpu,
            painter: new Painter()
        );

        node.Export = export;
        gpu.Recording = true;
        Assert.True(condition: Install(node: node), userMessage: node.LastSwapError?.Message);

        var image = export.Created[0].ImageHandle;

        for (var frame = 0; (frame < 2); frame++) {
            _ = node.ProduceFrame(context: default);
        }

        gpu.Recording = false;

        var transitions = gpu.Barriers
            .Where(predicate: barrier => (barrier.Handle == image))
            .Select(selector: static barrier => (barrier.Barrier.OldLayout, barrier.Barrier.NewLayout))
            .ToArray();

        Assert.Equal(
            actual: transitions,
            expected: [
                (GpuImageLayout.Undefined, GpuImageLayout.TransferDestination),
                (GpuImageLayout.TransferDestination, GpuImageLayout.External),
                (GpuImageLayout.External, GpuImageLayout.TransferDestination),
                (GpuImageLayout.TransferDestination, GpuImageLayout.External),
                (GpuImageLayout.External, GpuImageLayout.TransferDestination),
                (GpuImageLayout.TransferDestination, GpuImageLayout.External),
            ]
        );
    }

    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, Painter painter) {
        var packages = new RenderGraphPackageRecorders();

        packages.Register(
            factory: painter,
            package: Paint
        );

        var node = new ShaderPipelineRenderNode(
            deviceContext: gpu,
            height: 8,
            hostsOnDirectX: false,
            name: "exported",
            outputLayout: GpuImageLayout.ShaderReadOnly,
            packages: packages,
            pipelines: new GpuPassPipelineCache(),
            width: 8
        );
        var plan = new RenderGraphCompiler(packages: Catalog).Compile(definition: new RenderGraphDefinition(
            Name: "paint",
            Outputs: ["color"],
            Packages: [new RenderGraphPackagePass(
                Name: "paint",
                Outputs: [new ResourceReference(Name: "color")],
                Package: Paint
            )],
            Resources: [new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: nameof(GpuPixelFormat.R8G8B8A8Unorm),
                Name: "color"
            )],
            Schema: RenderGraphSchemas.Graph
        ));

        node.Swap(pipeline: new CompiledShaderPipeline(
            plan: plan.Pipeline,
            shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
        ));

        return node;
    }
    // Produces frames until the node has installed its graph and rendered once.
    private static bool Install(ShaderPipelineRenderNode node) => SpinWait.SpinUntil(
        condition: () => {
            if (node.FrameCounter >= 1) {
                return true;
            }

            _ = node.ProduceFrame(context: default);

            return false;
        },
        timeout: TimeSpan.FromSeconds(value: 30)
    );

    // A package that notes the image its output is written into.
    private sealed class Painter : IRenderGraphPackageFactory {
        public List<nint> Written { get; } = [];

        public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => null;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this);

        private sealed class Recorder(Painter owner) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Written.Add(item: recording.Outputs[0].Image.ImageHandle);

                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
