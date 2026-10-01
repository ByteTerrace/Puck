using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for package fragments, transient storage and counted storage. A fragment package's passes are spliced in place of
/// the pass naming it, its versions renamed after that pass, a name standing for an input port reading the version the
/// pass binds there, and the version a pass binds to an output port forwarding what the fragment's version forwards; a
/// bound output that forwards anything itself is refused. A transient storage is refused when anything could read what
/// an earlier frame left in it, and a node allocates it once while every other storage it owns is allocated once per
/// frame slot. A counted buffer is allocated at its count resolved against the node's counter at the extent it is built
/// for, and the installed graph is rebuilt at the new size when the counter's revision moves. A graph a fragment pass of
/// which counts its kernels' work keeps kernel counters, clears them ahead of every pass and copies them behind the last.
/// </summary>
public sealed class RenderGraphFragmentLawTests {
    private const string Counting = "test.counting";
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
    // A fragment of two passes, the first of whose kernels count their own work: the first writes a color the second
    // continues into the output port's version.
    private static RenderGraphPackage CountingPackage { get; } = new(
        Fragment: new RenderGraphPackageFragment(
            InputVersions: [],
            OutputVersions: ["final"],
            Passes: [
                new RenderGraphFragmentPass(
                    CountsKernelWork: true,
                    InputAccesses: [],
                    Inputs: [],
                    Name: "count",
                    OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
                    Outputs: ["color"]
                ),
                new RenderGraphFragmentPass(
                    InputAccesses: [],
                    Inputs: [],
                    Name: "plain",
                    OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
                    Outputs: ["final"]
                ),
            ],
            Resources: [
                Image(name: "color"),
                Image(from: "color", name: "final"),
            ]
        ),
        Id: Counting,
        Inputs: [],
        Members: [],
        Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
        Summary: "A two-pass fragment whose first pass counts its kernels' work."
    );
    private static RenderGraphPackageCatalog Catalog { get; } = new(packages: [
        FragmentPackage,
        CountingPackage,
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
    public void ACountChangeHoldsTheLastImageUntilTheCurrentRevisionIsBuilt() {
        var plan = Plan(definition: Chain(buffer: Buffer(name: "scratch", transient: true)));
        var gpu = new FakePipelineGpu();
        var counter = new Counter(instances: 2);
        using var node = Node(counter: counter, gpu: gpu, plan: plan);

        node.ProduceUntilInstalled();

        var submitted = node.FrameCounter;
        using var gate = new ManualResetEventSlim(initialState: false);
        using var entered = new ManualResetEventSlim(initialState: false);

        counter.BuildGate = gate;
        counter.BuildEntered = entered;
        counter.Instances = 7;

        try {
            _ = node.ProduceFrame(context: default);
            Assert.True(condition: entered.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(expected: submitted, actual: node.FrameCounter);
            // Another residency can have identical capacities, but its recorders belong to another revision.
            counter.Instances = 7;
        } finally {
            gate.Set();
        }

        TestLiveness.Until(
            step: () => {
                _ = node.ProduceFrame(context: default);

                return (node.FrameCounter > submitted);
            }
        );
        Assert.DoesNotContain(expected: 1L, collection: counter.InstalledRevisions);
        Assert.Contains(expected: 2L, collection: counter.InstalledRevisions);
        Assert.Equal(expected: ((Elements * 7) * 4), actual: node.ResourceStatus.Single(predicate: static status => (status.Name == "scratch")).AllocationBytes);
    }
    [Fact]
    public void ARefusedCountChangeNeitherRecordsNorRetriesUntilItsInputsChange() {
        var plan = Plan(definition: Chain(buffer: Buffer(name: "scratch", transient: true)));
        var gpu = new FakePipelineGpu();
        var counter = new Counter(instances: 2);
        using var node = Node(counter: counter, gpu: gpu, plan: plan);

        node.ProduceUntilInstalled();

        var submitted = node.FrameCounter;

        node.BudgetCapBytes = 1;
        counter.Instances = 7;
        _ = node.ProduceFrame(context: default);
        var refusal = node.LastSwapError;

        Assert.NotNull(@object: refusal);

        for (var frame = 0; (frame < 3); frame++) {
            _ = node.ProduceFrame(context: default);
            Assert.Equal(expected: submitted, actual: node.FrameCounter);
            Assert.Same(expected: refusal, actual: node.LastSwapError);
        }

        counter.Instances = 8;
        _ = node.ProduceFrame(context: default);
        Assert.NotSame(expected: refusal, actual: node.LastSwapError);
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
    // A graph a pass of which counts its kernels' work keeps a counter and a readback buffer a frame slot, one 16-byte row a
    // pass. Every frame clears the slot's counters ahead of every pass and copies them into its readback behind the last,
    // each with its buffer barrier, and hands the counting pass its row; a pass that does not count gets none, and a graph
    // with no counting pass keeps no counters.
    [Fact]
    public void AFrameClearsItsKernelCountersAheadOfEveryPassAndCopiesThemBehindTheLast() {
        var plan = Plan(definition: Graph(
            outputs: ["out"],
            packages: new RenderGraphPackagePass(
                Name: "counted",
                Outputs: ["out"],
                Package: Counting
            ),
            resources: [Image(name: "out")]
        ));

        Assert.True(condition: plan.Pipeline.CountsKernelWork);
        Assert.Equal(
            actual: plan.Pipeline.Passes.Select(selector: static pass => (pass.Name, pass.Package!.CountsKernelWork)),
            expected: [("counted$count", true), ("counted$plain", false)]
        );
        Assert.False(condition: Plan(definition: Chain(buffer: Buffer(name: "scratch", transient: true))).Pipeline.CountsKernelWork);

        var gpu = new FakePipelineGpu();
        var recordings = new List<(string Pass, GpuKernelCounterRow? Counters)>();
        using var node = Node(counter: null, gpu: gpu, plan: plan, recordings: recordings);

        node.ProduceUntilInstalled();

        // Three frame slots of a counter and a readback, two rows of 16 bytes each.
        var rowBytes = ((ulong)(2 * GpuKernelCounters.RowBytes));

        Assert.Equal(
            actual: gpu.CreatedObjects.Count(predicate: created => ((created.Kind == "buffer") && (created.Bytes == rowBytes))),
            expected: 6
        );

        gpu.Recording = true;
        recordings.Clear();
        node.ProduceFrame(context: default);

        var counted = recordings.Single(predicate: static recording => (recording.Pass == "counted$count")).Counters!.Value;
        var counter = counted.Buffer.BufferHandle;

        Assert.Equal(actual: counted.Row, expected: 0U);
        Assert.Null(@object: recordings.Single(predicate: static recording => (recording.Pass == "counted$plain")).Counters);

        var frame = gpu.Events.Where(predicate: static line => (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "clear buffer") || line.StartsWith(comparisonType: StringComparison.Ordinal, value: "copy buffer") || line.StartsWith(comparisonType: StringComparison.Ordinal, value: "record "))).ToArray();

        Assert.Equal(actual: frame[0], expected: $"clear buffer {counter}");
        Assert.Equal(actual: frame[1..^1], expected: ["record counted$count", "record counted$plain"]);
        Assert.StartsWith(actualString: frame[^1], expectedStartString: $"copy buffer {counter} to ");
        Assert.Equal(
            actual: gpu.Barriers.Where(predicate: barrier => (barrier.Handle == counter)).Select(selector: static barrier => (barrier.Barrier.SourceAccess, barrier.Barrier.DestinationAccess)),
            expected: [
                (GpuAccess.TransferWrite, GpuAccess.ShaderRead | GpuAccess.ShaderWrite),
                (GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuAccess.TransferRead),
            ]
        );
        Assert.Equal(
            actual: (Owned: node.OwnedBytes, Steady: node.InstalledAccount.SteadyBytes),
            expected: (Owned: gpu.LiveBytes, Steady: gpu.LiveBytes)
        );
    }
    // A package pass that skips the frame records nothing and counts as skipped in its submission, never as a pass that
    // ran and did nothing, so a required zero recorded for a skipped pass cannot be met by one that ran.
    [Fact]
    public void APackagePassSkippingTheFrameCountsAsSkipped() {
        var plan = Plan(definition: Graph(
            outputs: ["out"],
            packages: new RenderGraphPackagePass(
                Name: "counted",
                Outputs: ["out"],
                Package: Counting
            ),
            resources: [Image(name: "out")]
        ));
        var gpu = new FakePipelineGpu();
        var recordings = new List<(string Pass, GpuKernelCounterRow? Counters)>();
        using var node = Node(counter: null, gpu: gpu, plan: plan, recordings: recordings, skipped: "counted$plain");

        node.ProduceUntilInstalled();
        recordings.Clear();

        var sample = new GpuWorkSample();

        for (var frame = 0; ((frame < 8) && !node.TryReadCompleted(sample: sample)); frame++) {
            node.ProduceFrame(context: default);
        }

        var labels = sample.PassLabels.ToArray();

        Assert.Equal(
            actual: labels.Select(selector: (label, pass) => (label, sample.GetPassState(pass: pass))),
            expected: [("counted$count", GpuPassState.Executed), ("counted$plain", GpuPassState.Skipped)]
        );
        Assert.DoesNotContain(collection: recordings, filter: static recording => (recording.Pass == "counted$plain"));
    }

    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, RenderGraphPlan plan, IShaderPipelineStorageCounter? counter, List<(string Pass, GpuKernelCounterRow? Counters)>? recordings = null, string? skipped = null) {
        var packages = new RenderGraphPackageRecorders();

        packages.Register(factory: new Silent(counter: counter), package: Writer);
        packages.Register(factory: new Silent(counter: null), package: Reader);
        packages.Register(factory: new Silent(counter: null, gpu: gpu, recordings: recordings, skipped: skipped), package: Counting);

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

        public ManualResetEventSlim? BuildEntered { get; set; }
        public ManualResetEventSlim? BuildGate { get; set; }
        public long Revision { get; private set; }

        public List<long> InstalledRevisions { get; } = [];

        public ShaderPipelineStorageCounts CountsAt(uint width, uint height) => new(
            Height: height,
            Width: width
        ) {
            Instances = Instances,
        };
    }
    // A package that builds nothing and records nothing but says it drew, counting its instance's storages by a counter,
    // and, given a list, noting each recording's pass and work counters in it and in the device's events. The pass it
    // is told to skip skips every frame.
    private sealed class Silent(IShaderPipelineStorageCounter? counter, FakePipelineGpu? gpu = null, List<(string Pass, GpuKernelCounterRow? Counters)>? recordings = null, string? skipped = null) : IRenderGraphPackageFactory {
        public IShaderPipelineStorageCounter? CounterOf(string instance) => counter;
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
            if (counter is not Counter control) {
                return ValueTask.FromResult<IDisposable?>(result: null);
            }

            var revision = control.Revision;

            control.BuildEntered?.Set();
            control.BuildGate?.Wait(cancellationToken: cancellationToken);

            return ValueTask.FromResult<IDisposable?>(result: new Built(Revision: revision));
        }
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
            if ((counter is Counter control) && (built is Built revision)) {
                control.InstalledRevisions.Add(item: revision.Revision);
            }

            return new Recorder(
                gpu: gpu,
                pass: context.Pass,
                recordings: recordings,
                skips: string.Equals(a: context.Pass, b: skipped, comparisonType: StringComparison.Ordinal)
            );
        }

        private sealed record Built(long Revision) : IDisposable {
            public void Dispose() { }
        }
        private sealed class Recorder(FakePipelineGpu? gpu, string pass, List<(string Pass, GpuKernelCounterRow? Counters)>? recordings, bool skips) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public bool Skips(in FrameContext context) => skips;
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                recordings?.Add(item: (pass, recording.WorkCounters));

                if (gpu is { Recording: true }) {
                    gpu.Events.Add(item: $"record {pass}");
                }

                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
