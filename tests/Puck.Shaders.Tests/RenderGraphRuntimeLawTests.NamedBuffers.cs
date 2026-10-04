using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Theory]
    [InlineData(null)]
    [InlineData("secondary")]
    public void NamedAndDefaultBufferReadsFollowTheProducedRingAndStandingFrame(string? selected) {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var producer = new NamedBufferPackage();
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        var set = NamedBufferInstances();
        var reader = NamedBufferReader(selected);
        using var runtime = Runtime(gpu, recorders, set, "main", null!, reader);
        var frames = new Frames(runtime, [new RenderGraphRoot(Instance: "main", Width: 1, Height: 1)], []);
        frames.Settle();
        var handles = new HashSet<nint>();

        for (var frame = 0; frame < 6; frame++) {
            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            frames.Next();
            gpu.Recording = false;
            var produced = producer.Written[selected ?? "primary"];
            Assert.Equal(produced, Assert.Single(gpu.DescriptorWrites, write => write.Binding == 1).Handle);
            handles.Add(produced);
        }
        Assert.Equal(3, handles.Count);
        runtime.Node(0).Paused = true;
        var held = producer.Written["secondary"];
        Assert.True(runtime.TryInstall("main", reader with { Inputs = [new(Version: "pool", Producer: "pool", Output: "secondary")] }, out var refusal), refusal?.Message);
        for (var frame = 0; frame < 4; frame++) {
            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            frames.Next();
            gpu.Recording = false;
            Assert.Equal(held, Assert.Single(gpu.DescriptorWrites, write => write.Binding == 1).Handle);
        }
    }

    [Theory]
    [InlineData("private", 256UL, null, RenderGraphRuntimeRefusalCode.InputOutput)]
    [InlineData("missing", 256UL, null, RenderGraphRuntimeRefusalCode.InputOutput)]
    [InlineData("secondary", 512UL, null, RenderGraphRuntimeRefusalCode.InputSize)]
    [InlineData("secondary", 256UL, 4u, RenderGraphRuntimeRefusalCode.InputSize)]
    public void NamedBufferImportsRefuseUnexportedVersionsAndIncompatibleStorage(string output, ulong bytes, uint? stride, RenderGraphRuntimeRefusalCode code) {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders("named.read");
        recorders.Registry.Register(factory: new NamedBufferPackage(), package: RenderGraphPackageCatalog.Indirect);
        Assert.False(RenderGraphRuntime.TryCreate(NamedBufferInstances(), [null, NamedBufferReader(output, bytes, stride)], "main",
            recorders.Registry, new GpuPassPipelineCache(), gpu, false, out var runtime, out var refusal));
        Assert.Null(runtime);
        Assert.Equal(code, refusal!.Code);
    }

    [Fact]
    public void AReplacementCannotWithdrawANamedBufferStillReadByItsConsumer() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var producer = new NamedBufferPackage();
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        var set = NamedBufferInstances();
        using var runtime = Runtime(gpu, recorders, set, "main", null!, NamedBufferReader("secondary"));
        var frames = new Frames(runtime, [new RenderGraphRoot(Instance: "main", Width: 1, Height: 1)], []);
        frames.Settle();
        var original = producer.Fragment;
        producer.Fragment = original with { OutputVersions = ["primary"] };
        Assert.False(runtime.TryReconfigure(set, [null, null], "main", out var refusal));
        Assert.Equal(RenderGraphRuntimeRefusalCode.InputOutput, refusal!.Code);
        Assert.Throws<InvalidDataException>(() => frames.Next());
        Assert.False(runtime.Node(0).HasPendingCandidate);
        producer.Fragment = original;
        frames.Next();
        Assert.Null(runtime.Node(0).LastSwapError);
    }

    [Fact]
    public void ATransferInstanceCannotBindAPreviousBufferPublication() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders("named.read");
        recorders.Registry.Register(factory: new NamedBufferPackage(), package: RenderGraphPackageCatalog.Indirect);
        var instances = NamedBufferInstances().Instances.ToArray();
        instances[1] = instances[1] with { Reads = [new("pool", Kind: ShaderPipelineResourceKind.Buffer, PreviousFrame: true)] };
        Assert.False(RenderGraphRuntime.TryCreate(Set(instances), [null, NamedBufferReader("secondary", transfer: true)], "main",
            recorders.Registry, new GpuPassPipelineCache(), gpu, false, out var runtime, out var refusal));
        Assert.Null(runtime);
        Assert.Equal(RenderGraphRuntimeRefusalCode.TransferInput, refusal!.Code);
    }

    [Fact]
    public void AnExportedBorrowedIntermediateLetsItsUnchangedDependentPassStand() {
        var gpu = new FakePipelineGpu();
        using var primary = gpu.Services.BufferFactory.CreateDeviceLocal(PoolBytes, GpuBufferUsage.Storage, new GpuObjectName("test", "primary"));
        using var secondary = gpu.Services.BufferFactory.CreateDeviceLocal(PoolBytes, GpuBufferUsage.Storage, new GpuObjectName("test", "secondary"));
        using var scratch = gpu.Services.BufferFactory.CreateDeviceLocal(PoolBytes, GpuBufferUsage.Storage, new GpuObjectName("test", "private"));
        var producer = new NamedBufferPackage {
            Buffers = new Dictionary<string, IGpuBuffer>(StringComparer.Ordinal) { ["primary"] = primary, ["secondary"] = secondary, ["private"] = scratch },
            Signature = 1,
        };
        producer.Fragment = producer.Fragment with {
            Passes = [.. producer.Fragment.Passes.Select(pass => pass.Name == "secondary"
                ? pass with { Inputs = ["primary"], InputAccesses = [RenderGraphPortAccess.ComputeRead] } : pass)],
        };
        var recorders = new Recorders();
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        using var runtime = Runtime(gpu, recorders, NamedBufferInstances(), "main", null!, NamedBufferReader("secondary"));
        var frames = new Frames(runtime, [new RenderGraphRoot(Instance: "main", Width: 1, Height: 1)], []);
        frames.Settle();
        frames.Next(6);
        Assert.Equal(1, producer.Records["primary"]);
        Assert.Equal(1, producer.Records["secondary"]);
        Assert.Equal(secondary.BufferHandle, producer.Written["secondary"]);
    }

    private static RenderGraphInstanceSet NamedBufferInstances() => Set(
        new RenderGraphInstance(Name: "pool", ExternalPackage: RenderGraphPackageCatalog.Indirect,
            Output: ShaderPipelineResourceKind.Buffer, Passes: 3, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        Instance("main", reads: new RenderGraphRead("pool", Kind: ShaderPipelineResourceKind.Buffer)));

    private static RenderGraphRuntimeGraph NamedBufferReader(string? output, ulong bytes = PoolBytes, uint? stride = null, bool transfer = false) {
        if (stride is not null || transfer) {
            var catalog = new RenderGraphPackageCatalog([new(Id: "named.read", Members: [], Summary: "Reads a typed buffer.",
                Inputs: [RenderGraphPackagePort.Buffer(transfer ? RenderGraphPortAccess.TransferRead : RenderGraphPortAccess.ComputeRead, stride, null)],
                Outputs: [RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite)])]);
            var plan = new RenderGraphCompiler(catalog).Compile(new RenderGraphDefinition(Name: "typed-reader", Schema: RenderGraphSchemas.Graph,
                Resources: [Image("image"), new(Name: "pool", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: bytes,
                    StrideBytes: stride, Initialization: ShaderPipelineInitialization.External)], Outputs: ["image"],
                Packages: [new(Name: "read", Package: "named.read", Inputs: ["pool"], Outputs: ["image"])]));
            return new(Pipeline: new CompiledShaderPipeline(plan.Pipeline, new Dictionary<string, CompiledShader>()),
                Inputs: [new(Version: "pool", Producer: "pool", Output: output)]);
        }
        var pipeline = ScreensGraph(true);
        if (bytes != PoolBytes) {
            pipeline = Compile(pipeline.Plan.Definition with {
                Resources = [.. pipeline.Plan.Definition.Resources.Select(resource => resource.Name == "pool"
                    ? resource with { SizeBytes = bytes, StrideBytes = stride } : resource)],
            });
        }
        return new(Pipeline: pipeline, Inputs: [new(Version: "pool", Producer: "pool", Output: output)]);
    }

    private sealed class NamedBufferPackage : IRenderGraphPackageFactory {
        public RenderGraphPackageFragment Fragment { get; set; } = new(
            InputVersions: [], OutputVersions: ["primary", "secondary"],
            Resources: [.. new[] { "primary", "secondary", "private" }.Select(static name =>
                new ShaderPipelineResource(Name: name, Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: PoolBytes))],
            Passes: [.. new[] { "primary", "secondary", "private" }.Select(static name =>
                new RenderGraphFragmentPass(Name: name, Inputs: [], InputAccesses: [], Outputs: [new(name)],
                    OutputAccesses: [RenderGraphPortAccess.ComputeWrite]))]);
        public Dictionary<string, nint> Written { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Records { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IGpuBuffer>? Buffers { get; init; }
        public ulong? Signature { get; init; }
        public bool OwnsBuffers => Buffers is not null;
        public IGpuBuffer? BorrowedBuffer(RenderGraphPackageRecorderContext context, IDisposable? built, ShaderPipelineResource resource) => Buffers?[context.Part!];
        public RenderGraphPackageFragment? FragmentOf(string instance) => Fragment;
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(this, context.Part!);
        private sealed class Recorder(NamedBufferPackage owner, string part) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) => owner.Signature;
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Records[part] = owner.Records.GetValueOrDefault(part) + 1;
                owner.Written[part] = recording.Outputs[0].Buffer!.BufferHandle;
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
