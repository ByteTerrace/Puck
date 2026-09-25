using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of <see cref="RenderGraphRuntime"/> on <see cref="FakePipelineGpu"/>: no device, no shader compiler. Cameras are
/// instances of a one-pass package graph whose fake recorder counts what it records; screens and panes are compute
/// graphs reading their producers through external versions; a mirror reads its own previous frame through its graph's
/// history. Every instance renders through its own node, so its renders are that node's submissions.
/// </summary>
public sealed partial class RenderGraphRuntimeLawTests {
    private const int Display = 64;
    private const string Camera = "test.camera";
    private const string Over = "test.over";
    private const string Pool = "test.pool";
    private const ulong PoolBytes = 256;

    private static readonly RenderGraphPackageCatalog Catalog = new(packages: [
        new RenderGraphPackage(
            Id: Camera,
            Inputs: [],
            Outputs: [RenderGraphPackagePort.Image],
            Summary: "A camera view whose recorder counts its renders."
        ),
        new RenderGraphPackage(
            Id: Over,
            Inputs: [RenderGraphPackagePort.Image],
            Outputs: [RenderGraphPackagePort.Image],
            Summary: "A pass drawn over its input, which may draw nothing."
        ),
        new RenderGraphPackage(
            Id: Pool,
            Inputs: [],
            Outputs: [RenderGraphPackagePort.Buffer(
                count: null,
                strideBytes: null
            )],
            Summary: "A raw buffer its readers bind."
        ),
    ]);

    private static CompiledShader Shader(string name) {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x03, 0x02, 0x23, 0x07 };
        var stages = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = bytecode };

        return new CompiledShader(
            diagnostics: [],
            dxil: stages,
            name: name,
            sourceHash: name,
            sourcePath: $"{name}.hlsl",
            spirv: stages
        );
    }
    private static CompiledShaderPipeline Compile(RenderGraphDefinition definition) {
        var plan = new RenderGraphCompiler(packages: Catalog).Compile(definition: definition);

        return new CompiledShaderPipeline(
            plan: plan.Pipeline,
            shaders: plan.Steps.Where(predicate: static step => (step.Package is null)).ToDictionary(
                elementSelector: static step => Shader(name: step.Name),
                keySelector: static step => step.Name
            )
        );
    }
    // A frame-relative RGBA8 image: every instance's images follow the extent the scheduler gives it.
    private static ShaderPipelineResource Image(string name, bool external = false, bool history = false) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: "R8G8B8A8Unorm",
        History: history,
        Initialization: (external
            ? ShaderPipelineInitialization.External
            : (history
                ? ShaderPipelineInitialization.Zero
                : ShaderPipelineInitialization.Undefined)),
        Name: name
    );
    // A camera: one package pass writing the image its consumers read.
    private static CompiledShaderPipeline CameraGraph() => Compile(definition: new RenderGraphDefinition(
        Name: "camera",
        Outputs: ["color"],
        Packages: [new RenderGraphPackagePass(
            Name: "view",
            Outputs: ["color"],
            Package: Camera
        )],
        Resources: [Image(name: "color")],
        Schema: RenderGraphSchemas.Graph
    ));
    // World-scoped work: one package pass writing a raw buffer its readers bind.
    private static CompiledShaderPipeline PoolGraph() => Compile(definition: new RenderGraphDefinition(
        Name: "pool",
        Outputs: ["pool"],
        Packages: [new RenderGraphPackagePass(
            Name: "fill",
            Outputs: ["pool"],
            Package: Pool
        )],
        Resources: [new ShaderPipelineResource(
            Kind: ShaderPipelineResourceKind.Buffer,
            Name: "pool",
            SizeBytes: PoolBytes
        )],
        Schema: RenderGraphSchemas.Graph
    ));
    // A view that shows its screens, and reads the pool when asked: one compute pass reading every screen at bindings
    // 0.., then the pool, and writing the image at the next binding.
    private static CompiledShaderPipeline ScreensGraph(bool pool, params string[] screens) {
        var inputs = screens.Select(selector: static (screen, index) => new ResourceReference(
            Binding: ((uint)index),
            Name: screen
        )).ToList();
        var resources = screens.Select(selector: static screen => Image(
            external: true,
            name: screen
        )).Append(element: Image(name: "image")).ToList();

        if (pool) {
            inputs.Add(item: new ResourceReference(
                Binding: ((uint)screens.Length),
                Name: "pool"
            ));
            resources.Add(item: new ShaderPipelineResource(
                Initialization: ShaderPipelineInitialization.External,
                Kind: ShaderPipelineResourceKind.Buffer,
                Name: "pool",
                SizeBytes: PoolBytes
            ));
        }

        return Compile(definition: new RenderGraphDefinition(
            Name: "screens",
            Outputs: ["image"],
            Passes: [new ShaderPipelinePass(
                EntryPoint: "main",
                Inputs: inputs,
                Kind: ShaderPipelineDocumentPassKind.Compute,
                Name: "compose",
                Outputs: [new ResourceReference(
                    Binding: ((uint)inputs.Count),
                    Name: "image"
                )],
                Source: "compose.hlsl"
            )],
            Resources: resources,
            Schema: RenderGraphSchemas.Graph
        ));
    }
    // A mirror facing itself: one compute pass reading the frame's previous contents and writing them again.
    private static CompiledShaderPipeline MirrorGraph() => Compile(definition: new RenderGraphDefinition(
        Name: "mirror",
        Outputs: ["frame"],
        Passes: [new ShaderPipelinePass(
            EntryPoint: "main",
            Inputs: [new ResourceReference(
                Binding: 0,
                Name: "frame",
                PreviousFrame: true
            )],
            Kind: ShaderPipelineDocumentPassKind.Compute,
            Name: "reflect",
            Outputs: [new ResourceReference(
                Binding: 1,
                Name: "frame"
            )],
            Source: "reflect.hlsl"
        )],
        Resources: [Image(
            history: true,
            name: "frame"
        )],
        Schema: RenderGraphSchemas.Graph
    ));
    private static RenderGraphInstance Instance(string name, ShaderPipelineResourceKind output = ShaderPipelineResourceKind.Image, params RenderGraphRead[] reads) => new(
        Name: name,
        Output: output,
        Passes: 1,
        Reads: reads,
        Refresh: RenderGraphRefresh.EveryFrame
    );
    private static RenderGraphInstanceSet Set(params RenderGraphInstance[] instances) {
        Assert.True(
            condition: RenderGraphInstanceSet.TryCreate(
                instances: instances,
                refusal: out var refusal,
                set: out var set
            ),
            userMessage: refusal?.Message
        );

        return set;
    }
    private static RenderGraphRuntime Runtime(FakePipelineGpu gpu, Recorders recorders, RenderGraphInstanceSet set, string root, params RenderGraphRuntimeGraph[] graphs) {
        Assert.True(
            condition: RenderGraphRuntime.TryCreate(
                deviceContext: gpu,
                graphs: graphs,
                hostsOnDirectX: false,
                packages: recorders.Registry,
                refusal: out var refusal,
                root: root,
                runtime: out var runtime,
                set: set
            ),
            userMessage: refusal?.Message
        );

        return runtime;
    }
    private static RenderGraphRuntimeGraph Graph(CompiledShaderPipeline pipeline, params (string Version, string Producer)[] inputs) => new(
        Inputs: [.. inputs.Select(selector: static input => new RenderGraphRuntimeInput(
            Producer: input.Producer,
            Version: input.Version
        ))],
        Pipeline: pipeline
    );

    /// <summary>The fake recorders and what they recorded, per instance.</summary>
    private sealed class Recorders : IRenderGraphPackageFactory {
        public Recorders(params string[] serves) {
            foreach (var package in serves) {
                Registry.Register(
                    factory: this,
                    package: package
                );
            }
        }

        public IReadOnlyList<GpuComputeBinding> SetBindings => [];

        public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => null;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, nint descriptorPool) => Create(context: context);

        public Dictionary<string, Counter> ByInstance { get; } = new(comparer: StringComparer.Ordinal);
        public RenderGraphPackageRecorders Registry { get; } = new();

        public Counter Of(string instance) => (ByInstance.TryGetValue(
            key: instance,
            value: out var counter
        )
            ? counter
            : new Counter());

        private IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context) {
            if (!ByInstance.TryGetValue(
                key: context.Instance,
                value: out var counter
            )) {
                counter = new Counter();
                ByInstance.Add(
                    key: context.Instance,
                    value: counter
                );
            }

            counter.Created++;

            return new FakeRecorder(counter: counter);
        }
    }
    /// <summary>What one instance's package recorders recorded: creations, disposals, records, and the last record's
    /// extent, output buffer and output image layout.</summary>
    private sealed class Counter {
        public int Created;
        public int Disposed;
        public uint Height;
        public nint InputImage;
        public nint OutputBuffer;
        public nint OutputImage;
        public GpuImageLayout OutputLayout;
        public RenderGraphPackageOutcome Outcome;
        public long Records;
        public uint Width;
    }
    private sealed class FakeRecorder(Counter counter) : IRenderGraphPackageRecorder {
        public void Dispose() => counter.Disposed++;
        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            counter.Records++;
            counter.Width = recording.Width;
            counter.Height = recording.Height;
            counter.OutputBuffer = (recording.Outputs[0].Buffer?.BufferHandle ?? 0);
            counter.OutputLayout = recording.Outputs[0].Image.Layout;
            counter.InputImage = ((recording.Inputs.Length == 0)
                ? 0
                : recording.Inputs[0].Image.ImageHandle);
            counter.OutputImage = recording.Outputs[0].Image.ImageHandle;

            return counter.Outcome;
        }
    }
    /// <summary>Describes each frame to a runtime over the same roots and footprints, counting frame indices.</summary>
    private sealed class Frames(RenderGraphRuntime runtime, IReadOnlyList<RenderGraphRoot> roots, IReadOnlyList<RenderGraphFootprint> footprints) {
        public long Index { get; private set; }

        public Surface Next() {
            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: footprints,
                Index: Index++,
                Roots: roots
            );

            return runtime.ProduceFrame(
                context: default,
                frame: in frame
            );
        }
        public void Next(int count) {
            for (var frame = 0; (frame < count); frame++) {
                _ = Next();
            }
        }
        // Produces until every scheduled instance has built its graph and produced, then one frame more, so every
        // consumer has read a completed output of each producer.
        public void Settle() {
            Assert.True(
                condition: SpinWait.SpinUntil(
                    condition: () => {
                        _ = Next();

                        return runtime.IsSettled;
                    },
                    timeout: TimeSpan.FromSeconds(value: 30)
                ),
                userMessage: "The runtime's scheduled instances never all produced."
            );
            _ = Next();
        }
    }
}
