using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for package fragments, transient storage and counted storage. A fragment package's passes are spliced in place of
/// the pass naming it, its versions renamed after that pass, a name standing for an input port reading the version the
/// pass binds there, and the version a pass binds to an output port forwarding what the fragment's version forwards; a
/// bound output that forwards anything itself is refused. A transient storage is refused when anything could read what
/// an earlier frame left in it, and a node allocates it once while every other storage it owns is allocated once per
/// frame slot. A counted buffer is allocated at its count resolved against the node's counter at the extent it is built
/// for, and the installed graph is rebuilt at the new size when the counter's revision moves.
/// </summary>
public sealed class RenderGraphFragmentLawTests {
    private const ulong Elements = 5;
    private const uint Extent = 32;
    private const string Fragmented = "test.fragment";
    private const string Reader = "test.reader";
    private const string Writer = "test.writer";

    private static readonly IReadOnlyList<ShaderPipelineCountTerm> Counted = [new ShaderPipelineCountTerm(Elements: Elements, Per: [ShaderPipelineCountBasis.Instances])];
    // A fragment of three passes over its own scratch: a first pass writing it, an indirect second pass reading it and
    // the input port into its color, and a third continuing that color into the output port's version.
    private static RenderGraphPackage FragmentPackage { get; } = new(
        Fragment: new RenderGraphPackageFragment(
            InputVersions: ["source"],
            OutputVersions: ["final"],
            Passes: [
                new RenderGraphFragmentPass(
                    InputAccesses: [],
                    Inputs: [],
                    Name: "first",
                    OutputAccesses: [RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite],
                    Outputs: ["scratch", "arguments"]
                ),
                new RenderGraphFragmentPass(
                    Dispatch: ShaderPipelineDispatch.Indirect(arguments: "arguments"),
                    InputAccesses: [RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead],
                    Inputs: ["scratch", "source"],
                    Name: "second",
                    OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
                    Outputs: ["color"]
                ),
                new RenderGraphFragmentPass(
                    InputAccesses: [],
                    Inputs: [],
                    Name: "third",
                    OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
                    Outputs: ["final"]
                ),
            ],
            Resources: [
                Buffer(name: "scratch", transient: true),
                new ShaderPipelineResource(
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: "arguments",
                    SizeBytes: ShaderPipelineDispatch.ArgumentBytes,
                    StrideBytes: 4,
                    Transient: true
                ),
                Image(name: "color"),
                Image(from: "color", name: "final"),
            ]
        ),
        Id: Fragmented,
        Inputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeRead)],
        Members: [],
        Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
        Summary: "A three-pass fragment."
    );
    private static RenderGraphPackageCatalog Catalog { get; } = new(packages: [
        FragmentPackage,
        new RenderGraphPackage(
            Id: Writer,
            Inputs: [],
            Members: [],
            Outputs: [RenderGraphPackagePort.Buffer(access: RenderGraphPortAccess.ComputeWrite, count: Counted, strideBytes: 4)],
            Summary: "Writes a counted buffer."
        ),
        new RenderGraphPackage(
            Id: Reader,
            Inputs: [RenderGraphPackagePort.Buffer(access: RenderGraphPortAccess.ComputeRead, count: Counted, strideBytes: 4)],
            Members: [],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Summary: "Reads a counted buffer into an image."
        ),
    ]);

    private static ShaderPipelineResource Buffer(string name, bool transient, bool history = false) => new(
        Count: Counted,
        History: history,
        Initialization: (history
            ? ShaderPipelineInitialization.Zero
            : ShaderPipelineInitialization.Undefined),
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: name,
        StrideBytes: 4,
        Transient: transient
    );
    private static ShaderPipelineResource Image(string name, string? from = null, ShaderPipelineInitialization initialization = ShaderPipelineInitialization.Undefined) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: nameof(GpuPixelFormat.R8G8B8A8Unorm),
        From: from,
        Initialization: initialization,
        Name: name
    );
    private static RenderGraphDefinition Graph(IReadOnlyList<ShaderPipelineResource> resources, IReadOnlyList<string> outputs, params RenderGraphPackagePass[] packages) => new(
        Name: "fragments",
        Outputs: outputs,
        Packages: packages,
        Resources: resources,
        Schema: RenderGraphSchemas.Graph
    );
    private static RenderGraphPlan Plan(RenderGraphDefinition definition) => new RenderGraphCompiler(packages: Catalog).Compile(definition: definition);
    private static IReadOnlyList<ShaderPipelineDiagnostic> Refusals(RenderGraphDefinition definition) => Assert.Throws<ShaderPipelineCompilationException>(testCode: () => Plan(definition: definition)).Diagnostics;
    // The writer and the reader over one buffer the graph declares.
    private static RenderGraphDefinition Chain(ShaderPipelineResource buffer, bool previous = false) => Graph(
        outputs: ["image"],
        packages: [
            new RenderGraphPackagePass(
                Inputs: [new ResourceReference(Name: buffer.Name, PreviousFrame: previous)],
                Name: "read",
                Outputs: ["image"],
                Package: Reader
            ),
            new RenderGraphPackagePass(
                Name: "write",
                Outputs: [buffer.Name],
                Package: Writer
            ),
        ],
        resources: [buffer, Image(name: "image")]
    );

    [Fact]
    public void AFragmentsPassesAndVersionsAreSplicedAfterThePassNamingIt() {
        var plan = Plan(definition: Graph(
            outputs: ["out"],
            packages: new RenderGraphPackagePass(
                Inputs: ["in"],
                Name: "frag",
                Outputs: ["out"],
                Package: Fragmented
            ),
            resources: [Image(initialization: ShaderPipelineInitialization.External, name: "in"), Image(name: "out")]
        ));

        Assert.Equal(expected: ["frag$first", "frag$second", "frag$third"], actual: plan.Pipeline.PassOrder);
        Assert.Equal(expected: ["first", "second", "third"], actual: plan.Pipeline.Passes.Select(selector: static pass => pass.Package!.Part));
        Assert.All(action: static step => Assert.Equal(expected: Fragmented, actual: step.Package?.Id), collection: plan.Steps);

        var second = plan.Pipeline.Passes[1];

        // The input port's name reads the version the pass binds there, and the indirect arguments are renamed with the
        // fragment's versions.
        Assert.Equal(expected: ["frag$scratch", "in"], actual: second.Inputs.Select(selector: static input => input.Name));
        Assert.Equal(expected: ShaderPipelineDispatch.Indirect(arguments: "frag$arguments"), actual: second.Package!.Dispatch);
        Assert.Equal(expected: "frag$arguments", actual: second.Accesses[0].Version);

        // The output port's version forwards what the fragment's version forwards, so the chain is one storage.
        var out_ = plan.Pipeline.Storages.Single(predicate: static storage => storage.Versions.Contains(value: "out"));

        Assert.Equal(expected: ["frag$color", "out"], actual: out_.Versions);
        Assert.Equal(expected: ["in"], actual: plan.Inputs);
    }
    [Fact]
    public void AVersionBoundToAFragmentsOutputThatForwardsIsRefusedByName() {
        var refusal = Assert.Single(collection: Refusals(definition: Graph(
            outputs: ["out"],
            packages: new RenderGraphPackagePass(
                Inputs: ["in"],
                Name: "frag",
                Outputs: ["out"],
                Package: Fragmented
            ),
            resources: [
                Image(initialization: ShaderPipelineInitialization.External, name: "in"),
                Image(initialization: ShaderPipelineInitialization.Zero, name: "base"),
                Image(from: "base", name: "out"),
            ]
        )));

        Assert.Equal(expected: "RENDERGRAPH_PACKAGE_OUTPUT", actual: refusal.Code);
        Assert.Equal(expected: "out", actual: refusal.Name);
    }
    [Fact]
    public void ATransientStorageAnEarlierFrameCouldReachIsRefused() {
        Assert.Contains(
            collection: Refusals(definition: Chain(buffer: Buffer(history: true, name: "scratch", transient: true), previous: true)),
            filter: static refusal => (refusal.Code == "SHADERPIPE_TRANSIENT")
        );
        Assert.Contains(
            collection: Refusals(definition: Graph(
                outputs: ["scratch"],
                packages: new RenderGraphPackagePass(
                    Name: "write",
                    Outputs: ["scratch"],
                    Package: Writer
                ),
                resources: [Buffer(name: "scratch", transient: true)]
            )),
            filter: static refusal => ((refusal.Code == "SHADERPIPE_TRANSIENT") && refusal.Message.Contains(comparisonType: StringComparison.Ordinal, value: "is published"))
        );
        Assert.Contains(
            collection: Refusals(definition: Graph(
                outputs: ["image"],
                packages: new RenderGraphPackagePass(
                    Inputs: ["scratch"],
                    Name: "read",
                    Outputs: ["image"],
                    Package: Reader
                ),
                resources: [Buffer(name: "scratch", transient: true) with { Initialization = ShaderPipelineInitialization.Zero }, Image(name: "image")]
            )),
            filter: static refusal => (refusal.Code == "SHADERPIPE_TRANSIENT")
        );
    }
    [Fact]
    public void ATransientStorageIsOneAllocationAndItsFirstUseOrdersTheFrameBefore() {
        var plan = Plan(definition: Chain(buffer: Buffer(name: "scratch", transient: true)));
        var write = plan.Pipeline.Passes[0].Accesses.Single();

        // Its first use of a frame follows the previous frame's read of the one allocation.
        Assert.Equal(expected: ShaderPipelinePriorKind.CrossFrame, actual: write.PriorKind);
        Assert.Equal(expected: ShaderPipelineBarrierKind.Buffer, actual: write.Barrier.Kind);
        Assert.Equal(expected: GpuAccess.ShaderRead, actual: write.Barrier.SourceAccess);

        var gpu = new FakePipelineGpu();
        var counter = new Counter(instances: 3);
        using var node = Node(counter: counter, gpu: gpu, plan: plan);

        node.ProduceUntilInstalled();

        // Three frame slots of the image, and one allocation of the transient buffer, at its resolved count.
        Assert.Equal(expected: 3, actual: gpu.CreatedObjects.Count(predicate: static created => created.Kind.EndsWith(comparisonType: StringComparison.Ordinal, value: " image")));
        Assert.Equal(
            expected: [((Elements * 3) * 4)],
            actual: gpu.CreatedObjects.Where(predicate: static created => ((created.Kind == "buffer") && (created.Bytes == ((Elements * 3) * 4)))).Select(selector: static created => created.Bytes)
        );

        var scratch = gpu.CreatedObjects.Single(predicate: static created => ((created.Kind == "buffer") && (created.Bytes == ((Elements * 3) * 4)))).Handle;

        gpu.Recording = true;
        node.ProduceFrame(context: default);
        node.ProduceFrame(context: default);

        // Every frame's first use of the one allocation records its planned barrier on the same buffer.
        Assert.Equal(
            expected: 2,
            actual: gpu.Barriers.Count(predicate: barrier => ((barrier.Handle == scratch) && barrier.Barrier.DestinationAccess.HasFlag(flag: GpuAccess.ShaderWrite)))
        );
    }
    [Fact]
    public void ACountedBufferIsRebuiltAtItsNewCountWhenTheCounterMoves() {
        var plan = Plan(definition: Chain(buffer: Buffer(name: "scratch", transient: false)));
        var gpu = new FakePipelineGpu();
        var counter = new Counter(instances: 2);
        using var node = Node(counter: counter, gpu: gpu, plan: plan);

        node.ProduceUntilInstalled();

        Assert.Equal(expected: 3, actual: gpu.CreatedObjects.Count(predicate: static created => ((created.Kind == "buffer") && (created.Bytes == ((Elements * 2) * 4)))));

        counter.Instances = 7;
        node.ProduceBuildStart(gpu: gpu);
        node.ProduceFrame(context: default);

        Assert.Equal(expected: 3, actual: gpu.CreatedObjects.Count(predicate: static created => ((created.Kind == "buffer") && (created.Bytes == ((Elements * 7) * 4)))));
        Assert.Equal(expected: (((Elements * 7) * 4) * 3), actual: node.ResourceStatus.Single(predicate: static status => (status.Name == "scratch")).AllocationBytes);
    }
    [Fact]
    public void ACountedBufferANodeCannotCountIsRefusedByName() {
        var plan = Plan(definition: Chain(buffer: Buffer(name: "scratch", transient: false)));
        var gpu = new FakePipelineGpu();
        using var node = Node(counter: null, gpu: gpu, plan: plan);

        node.ProduceFrame(context: default);

        Assert.False(condition: node.IsReady);
        Assert.Contains(expectedSubstring: "'scratch'", actualString: node.LastSwapError?.Message);
    }

    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, RenderGraphPlan plan, IShaderPipelineStorageCounter? counter) {
        var packages = new RenderGraphPackageRecorders();

        packages.Register(factory: new Silent(counter: counter), package: Writer);
        packages.Register(factory: new Silent(counter: null), package: Reader);

        var node = new ShaderPipelineRenderNode(
            deviceContext: gpu,
            height: Extent,
            hostsOnDirectX: false,
            name: "fragments",
            outputLayout: GpuImageLayout.ShaderReadOnly,
            packages: packages,
            pipelines: new GpuPassPipelineCache(),
            width: Extent
        );

        node.Swap(pipeline: new CompiledShaderPipeline(
            plan: plan.Pipeline,
            shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
        ));

        return node;
    }

    // Counts the instances a law sets, moving its revision each time they change.
    private sealed class Counter(ulong instances) : IShaderPipelineStorageCounter {
        public ulong Instances {
            get;
            set {
                field = value;
                Revision++;
            }
        } = instances;
        public long Revision { get; private set; }

        public ShaderPipelineStorageCounts CountsAt(uint width, uint height) => new(
            Height: height,
            Width: width
        ) {
            Instances = Instances,
        };
    }
    // A package that builds nothing and records nothing but says it drew, counting its instance's storages by a counter.
    private sealed class Silent(IShaderPipelineStorageCounter? counter) : IRenderGraphPackageFactory {
        public IShaderPipelineStorageCounter? CounterOf(string instance) => counter;
        public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => null;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder();

        private sealed class Recorder : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) => RenderGraphPackageOutcome.Drew;
        }
    }
}
