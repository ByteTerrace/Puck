using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for an exported output (<see cref="IShaderPipelineOutputExport"/>): a node given an export renders its default
/// output into the one image the export creates, at the export's extent whatever extent it is asked for, publishes it in
/// <see cref="GpuImageLayout.External"/>, takes it back from its reader before the submission that writes it and completes
/// it after, and renders nothing on a frame the reader still holds it.
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
    public void AnExportedOutputIsTheExportsImageAtItsExtentHandedOffForItsReader() {
        var gpu = new FakePipelineGpu();
        var painter = new Painter();
        var export = new Export(gpu: gpu);
        using var node = Node(
            gpu: gpu,
            painter: painter
        );

        node.Export = export;
        node.Resize(
            height: 480,
            width: 640
        );

        Assert.True(condition: Render(node: node), userMessage: node.LastSwapError?.Message);
        Assert.Equal(expected: (ExportWidth, ExportHeight), actual: node.Extent);
        _ = Assert.Single(collection: export.Created);
        Assert.All(action: image => Assert.Equal(expected: export.Created[0].ImageHandle, actual: image), collection: painter.Written);
        Assert.Equal(expected: GpuImageLayout.External, actual: node.PublishedLayout);
        Assert.Equal(expected: export.Created[0].Writes, actual: export.Ended.Count);
        Assert.All(action: static ended => Assert.True(condition: ended.Written), collection: export.Ended);

        // The reader still holds the image: the node renders nothing and begins no write.
        var submissions = gpu.Submissions;
        var ended = export.Ended.Count;

        export.Released = false;
        _ = node.ProduceFrame(context: default);
        Assert.Equal(expected: submissions, actual: gpu.Submissions);
        Assert.Equal(expected: ended, actual: export.Ended.Count);
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
    // Produces frames until the node has installed its graph and rendered twice.
    private static bool Render(ShaderPipelineRenderNode node) => SpinWait.SpinUntil(
        condition: () => {
            _ = node.ProduceFrame(context: default);

            return (node.FrameCounter >= 2);
        },
        timeout: TimeSpan.FromSeconds(value: 30)
    );

    // An export over a fake image, which counts its writes and ends, and whose reader releases it until told otherwise.
    private sealed class Export(FakePipelineGpu gpu) : IShaderPipelineOutputExport {
        public List<Image> Created { get; } = [];
        public List<(bool Written, ulong Value)> Ended { get; } = [];
        public uint Height => ExportHeight;
        public bool Released { get; set; } = true;
        public uint Width => ExportWidth;

        public IGpuExportableImage Create(IGpuDeviceContext device) {
            var image = new Image(inner: gpu.Services.ImageFactory.Create(
                format: GpuPixelFormat.R8G8B8A8Unorm,
                height: Height,
                name: new GpuObjectName(owner: "export", part: "image"),
                usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
                width: Width
            ));

            Created.Add(item: image);

            return image;
        }
        public void EndWrite(bool written, IGpuExportableImage? image, ulong writtenValue) => Ended.Add(item: (written, writtenValue));
        public bool TryBeginWrite() => Released;
    }
    // A fake exportable image: a fake image whose writes count the fence values they signal.
    private sealed class Image(IGpuImage inner) : IGpuExportableImage {
        public GpuPixelFormat Format => inner.Format;
        public uint Height => inner.Height;
        public nint ImageHandle => inner.ImageHandle;
        public nint ImageViewHandle => inner.ImageViewHandle;
        public nint SharedFenceHandle => 1;
        public nint SharedHandle => 1;
        public GpuImageUsage Usage => inner.Usage;
        public uint Width => inner.Width;
        public int Writes { get; private set; }

        public void BeginWrite() { }
        public ulong CompleteWrite() => ((ulong)++Writes);
        public void Dispose() => inner.Dispose();
    }
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
