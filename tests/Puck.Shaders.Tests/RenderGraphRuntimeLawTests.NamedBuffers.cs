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
        runtime.Node(0).Paused = true;
        frames.Settle();
        Assert.Equal(1UL, runtime.Node(0).FrameCounter);
        runtime.Node(0).Paused = false;
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
        var pausedFrame = runtime.Node(0).FrameCounter;
        var held = producer.Written["secondary"];
        Assert.True(runtime.TryInstall("main", reader with { Inputs = [new(Version: "pool", Producer: "pool", Output: "secondary")] }, out var refusal), refusal?.Message);
        for (var frame = 0; frame < 4; frame++) {
            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            frames.Next();
            gpu.Recording = false;
            Assert.Equal(held, Assert.Single(gpu.DescriptorWrites, write => write.Binding == 1).Handle);
            Assert.Equal(pausedFrame, runtime.Node(0).FrameCounter);
        }
        runtime.Node(0).Step();
        gpu.DescriptorWrites.Clear();
        gpu.Recording = true;
        frames.Next();
        gpu.Recording = false;
        Assert.Equal(pausedFrame + 1, runtime.Node(0).FrameCounter);
        var stepped = producer.Written["secondary"];
        Assert.NotEqual(held, stepped);
        Assert.Equal(stepped, Assert.Single(gpu.DescriptorWrites, write => write.Binding == 1).Handle);
        frames.Next(4);
        Assert.Equal(pausedFrame + 1, runtime.Node(0).FrameCounter);
        Assert.Equal(stepped, producer.Written["secondary"]);

        runtime.Node(0).Reset();
        frames.Next();
        Assert.Equal(1UL, runtime.Node(0).FrameCounter);
        var initialized = producer.Written["secondary"];
        frames.Next(4);
        Assert.Equal(1UL, runtime.Node(0).FrameCounter);
        Assert.Equal(initialized, producer.Written["secondary"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("secondary")]
    public void PausedProducerReplacementsKeepPublishedNamedBuffersUntilTheirReadersRetire(string? selected) {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var producer = new NamedBufferPackage();
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        var set = NamedBufferInstances();
        var reader = NamedBufferReader(selected);
        using var runtime = Runtime(gpu, recorders, set, "main", null!, reader);
        var frames = new Frames(runtime, [new RenderGraphRoot(Instance: "main", Width: 1, Height: 1)], []);
        frames.Settle();
        var previous = new[] { producer.Written["primary"], producer.Written["secondary"] };
        frames.Next();
        var latest = new[] { producer.Written["primary"], producer.Written["secondary"] };
        var publications = previous.Concat(latest).Distinct().Select(handle =>
            Assert.Single(gpu.CreatedObjects, item => item.Handle == handle)).ToArray();
        Assert.Equal(4, publications.Length);
        var node = runtime.Node(0);
        node.Paused = true;
        var publishedFrame = node.FrameCounter;
        var held = producer.Written[selected ?? "primary"];
        ulong? boundedBytes = null;

        for (var replacement = 0; replacement < 4; replacement++) {
            var initialization = ((replacement % 2 == 0) ? ShaderPipelineInitialization.Zero : ShaderPipelineInitialization.Undefined);
            producer.Fragment = producer.Fragment with {
                Resources = [.. producer.Fragment.Resources.Select(resource => resource with { Initialization = initialization })],
            };
            Assert.True(runtime.TryReconfigure(set, [null, reader], "main", out var refusal), refusal?.Message);
            node.WaitForBuild();
            TestLiveness.Until(() => {
                gpu.DescriptorWrites.Clear();
                gpu.Recording = true;
                frames.Next();
                gpu.Recording = false;
                return !node.HasPendingCandidate && node.IsReady;
            });
            // Install binds the replacement's own descriptor sets too; observe the reader on the next held frame.
            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            frames.Next();
            gpu.Recording = false;
            Assert.Equal(publishedFrame, node.FrameCounter);
            Assert.Equal(held, Assert.Single(gpu.DescriptorWrites, write => write.Binding == 1).Handle);
            Assert.All(publications, item => Assert.Equal(0, item.DisposeCount));
            Assert.Equal(node.AllocationBytes + (4 * PoolBytes), node.OwnedBytes);
            boundedBytes ??= gpu.LiveBytes;
            Assert.Equal(boundedBytes.Value, gpu.LiveBytes);
        }
        node.Step();
        frames.Next();
        Assert.Equal(publishedFrame + 1, node.FrameCounter);
        Assert.NotEqual(held, producer.Written[selected ?? "primary"]);
        Assert.All(publications, item => Assert.Equal(0, item.DisposeCount));
        for (var step = 0; step < 3; step++) { node.Step(); frames.Next(); }
        frames.Next(2);
        Assert.All(publications, item => Assert.Equal(1, item.DisposeCount));
        Assert.Equal(node.AllocationBytes, node.OwnedBytes);

        node.Reset();
        frames.Next();
        Assert.Equal(1UL, node.FrameCounter);
        var reset = producer.Written[selected ?? "primary"];
        frames.Next(4);
        Assert.Equal(1UL, node.FrameCounter);
        Assert.Equal(reset, producer.Written[selected ?? "primary"]);
        runtime.Dispose();
        Assert.All(publications, item => Assert.Equal(1, item.DisposeCount));
        Assert.Empty(gpu.UsesAfterRelease);
    }

    [Fact]
    public void APausedBorrowedPublicationKeepsItsPackageOwnerUntilDisplacementAndFenceCompletion() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var producer = new NamedBufferPackage { BuildOwnsBuffers = true };
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        var set = NamedBufferInstances();
        var reader = NamedBufferReader("secondary");
        using var runtime = Runtime(gpu, recorders, set, "main", null!, reader);
        var frames = new Frames(runtime, [new RenderGraphRoot(Instance: "main", Width: 1, Height: 1)], []);
        frames.Settle();
        var original = new[] { producer.Written["primary"], producer.Written["secondary"] }
            .Select(handle => Assert.Single(gpu.CreatedObjects, item => item.Handle == handle)).ToArray();
        var held = producer.Written["secondary"];
        var node = runtime.Node(0);
        node.Paused = true;
        var publishedFrame = node.FrameCounter;
        ulong? boundedBytes = null;
        for (var replacement = 0; replacement < 4; replacement++) {
            var initialization = replacement % 2 == 0 ? ShaderPipelineInitialization.Zero : ShaderPipelineInitialization.Undefined;
            producer.Fragment = producer.Fragment with {
                Resources = [.. producer.Fragment.Resources.Select(resource => resource with { Initialization = initialization })],
            };
            Assert.True(runtime.TryReconfigure(set, [null, reader], "main", out var refusal), refusal?.Message);
            node.WaitForBuild();
            TestLiveness.Until(() => { frames.Next(); return !node.HasPendingCandidate && node.IsReady; });
            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            frames.Next();
            gpu.Recording = false;
            Assert.Equal(publishedFrame, node.FrameCounter);
            Assert.All(original, item => Assert.Equal(0, item.DisposeCount));
            Assert.Equal(held, Assert.Single(gpu.DescriptorWrites, write => write.Binding == 1).Handle);
            boundedBytes ??= gpu.LiveBytes;
            Assert.Equal(boundedBytes.Value, gpu.LiveBytes);
        }
        gpu.QueueHeld = true;
        for (var step = 0; step < 4; step++) { node.Step(); frames.Next(); }
        Assert.NotEqual(held, producer.Written["secondary"]);
        Assert.All(original, item => Assert.Equal(0, item.DisposeCount));
        gpu.QueueHeld = false;
        frames.Next(2);
        Assert.All(original, item => Assert.Equal(1, item.DisposeCount));
        runtime.Dispose();
        Assert.All(original, item => Assert.Equal(1, item.DisposeCount));
        Assert.Empty(gpu.UsesAfterRelease);
    }

    [Fact]
    public void PausedReplacementsSharingBorrowedAllocationsTransferTheirOwnerWithoutAccumulatingGraphs() {
        var gpu = new FakePipelineGpu();
        using var primary = new SharedBufferOwner(gpu.Services.BufferFactory.CreateDeviceLocal(PoolBytes, GpuBufferUsage.Storage, new GpuObjectName("test", "primary")));
        using var secondary = new SharedBufferOwner(gpu.Services.BufferFactory.CreateDeviceLocal(PoolBytes, GpuBufferUsage.Storage, new GpuObjectName("test", "secondary")));
        using var scratch = new SharedBufferOwner(gpu.Services.BufferFactory.CreateDeviceLocal(PoolBytes, GpuBufferUsage.Storage, new GpuObjectName("test", "private")));
        var producer = new NamedBufferPackage {
            SharedBuffers = new Dictionary<string, SharedBufferOwner>(StringComparer.Ordinal) {
                ["primary"] = primary, ["secondary"] = secondary, ["private"] = scratch,
            },
        };
        var recorders = new Recorders();
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        var set = NamedBufferInstances();
        var reader = NamedBufferReader("secondary");
        using var runtime = Runtime(gpu, recorders, set, "main", null!, reader);
        var frames = new Frames(runtime, [new RenderGraphRoot(Instance: "main", Width: 1, Height: 1)], []);
        frames.Settle();
        var node = runtime.Node(0);
        node.Paused = true;
        var publishedFrame = node.FrameCounter;
        var ownedBytes = node.OwnedBytes;
        var liveBytes = gpu.LiveBytes;
        for (var replacement = 0; replacement < 10; replacement++) {
            var initialization = replacement % 2 == 0 ? ShaderPipelineInitialization.Zero : ShaderPipelineInitialization.Undefined;
            producer.Fragment = producer.Fragment with {
                Resources = [.. producer.Fragment.Resources.Select(resource => resource with { Initialization = initialization })],
            };
            Assert.True(runtime.TryReconfigure(set, [null, reader], "main", out var refusal), refusal?.Message);
            node.WaitForBuild();
            TestLiveness.Until(() => { frames.Next(); return !node.HasPendingCandidate && node.IsReady; });
            frames.Next();
            Assert.Equal(publishedFrame, node.FrameCounter);
            Assert.Equal(secondary.Buffer.BufferHandle, producer.Written["secondary"]);
            Assert.Equal(2, primary.References);
            Assert.Equal(2, secondary.References);
            Assert.Equal(2, scratch.References);
            Assert.Equal(ownedBytes, node.OwnedBytes);
            Assert.Equal(liveBytes, gpu.LiveBytes);
        }
        runtime.Dispose();
        Assert.Equal(1, primary.References);
        Assert.Equal(1, secondary.References);
        Assert.Equal(1, scratch.References);
        Assert.Empty(gpu.UsesAfterRelease);
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
        public IReadOnlyDictionary<string, SharedBufferOwner>? SharedBuffers { get; init; }
        public bool BuildOwnsBuffers { get; init; }
        public ulong? Signature { get; init; }
        public bool OwnsBuffers => BuildOwnsBuffers || Buffers is not null || SharedBuffers is not null;
        public IGpuBuffer? BorrowedBuffer(RenderGraphPackageRecorderContext context, IDisposable? built, ShaderPipelineResource resource) =>
            built is BorrowedBuild owner ? owner.Buffer : Buffers?[context.Part!];
        public RenderGraphPackageFragment? FragmentOf(string instance) => Fragment;
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
            if (SharedBuffers is not null) { return ValueTask.FromResult<IDisposable?>(SharedBuffers[context.Part!].Retain()); }
            if (!BuildOwnsBuffers) { return ValueTask.FromResult<IDisposable?>(null); }
            var buffer = context.Services.BufferFactory.CreateDeviceLocal(PoolBytes, GpuBufferUsage.Storage, new GpuObjectName("test", context.Part!));
            return ValueTask.FromResult<IDisposable?>(new BorrowedBuild(buffer, buffer));
        }
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(this, context.Part!, built);
        private sealed class Recorder(NamedBufferPackage owner, string part, IDisposable? built) : IRenderGraphPackageRecorder {
            public void Dispose() => built?.Dispose();
            public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) => owner.Signature;
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Records[part] = owner.Records.GetValueOrDefault(part) + 1;
                owner.Written[part] = recording.Outputs[0].Buffer!.BufferHandle;
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
    private sealed class BorrowedBuild(IGpuBuffer buffer, IDisposable owner) : IDisposable {
        public IGpuBuffer Buffer { get; } = buffer;
        public void Dispose() => owner.Dispose();
    }
    private sealed class SharedBufferOwner(IGpuBuffer buffer) : IDisposable {
        public IGpuBuffer Buffer { get; } = buffer;
        public int References { get; private set; } = 1;
        public BorrowedBuild Retain() { References++; return new BorrowedBuild(Buffer, this); }
        public void Dispose() { if (--References == 0) { Buffer.Dispose(); } }
    }
}
