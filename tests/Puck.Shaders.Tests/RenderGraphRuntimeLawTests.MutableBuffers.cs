using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void ABorrowedProducerReacquiresConsumerWritesEvenWhenItsFirstPassSkips() {
        const ulong Bytes = (1024 * 1024);
        var gpu = new FakePipelineGpu { Recording = true };
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(Bytes, GpuBufferUsage.Storage, new GpuObjectName("test", "cache"));
        var recorders = new Recorders();
        var producer = new BorrowedPackage(buffer: buffer);
        var view = new BufferViewPackage(bytes: Bytes);

        view.Fragment = view.Fragment with {
            Passes = [.. view.Fragment.Passes.Select(selector: pass => ((pass.Name == SdfWorldPackage.Parts.Views) ? pass with {
                InputAccesses = [.. pass.Inputs.Select(selector: (input, index) => ((input.Name == SdfWorldPackage.IndirectCache)
                    ? RenderGraphPortAccess.ComputeReadWrite : pass.InputAccesses[index]))],
            } : pass))],
        };
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        var set = Set(PackageInstance() with { Reads = [new("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        using var runtime = Runtime(gpu, recorders, set, PackageView, new RenderGraphRuntimeGraph[2]);
        var frame = 0L;

        TestLiveness.Until(() => { ProducePackageFrame(runtime, frame++); return (runtime.Node(instance: 0).FrameCounter > 2); });
        producer.SkipPlacement = true;
        gpu.Barriers.Clear();
        ProducePackageFrame(runtime, frame++);
        var barriers = gpu.Barriers.Where(predicate: item => (item.Handle == buffer.BufferHandle)).Select(selector: item => item.Barrier).ToArray();

        Assert.NotEmpty(collection: barriers);
        Assert.True(condition: barriers[0].SourceAccess.HasFlag(flag: GpuAccess.ShaderWrite));
        Assert.True(condition: barriers[0].SourceAccess.HasFlag(flag: GpuAccess.TransferWrite));
        Assert.True(condition: barriers[0].SourceStage.HasFlag(flag: GpuStage.ComputeShader));
        Assert.True(condition: barriers[0].SourceStage.HasFlag(flag: GpuStage.Transfer));
        Assert.Contains(collection: barriers, filter: barrier => (barrier.DestinationAccess == (GpuAccess.ShaderRead | GpuAccess.ShaderWrite)));
        Assert.All(producer.Outputs, output => Assert.Same(actual: output, expected: buffer));
        Assert.True(condition: (runtime.Node(instance: 1).AllocationBytes < Bytes));
    }
    [InlineData(true, true)]
    [InlineData(false, false)]
    [Theory]
    public void AMutableEdgeRefusesHistoryAndGraphOwnedBufferRings(bool previous, bool borrowed) {
        var gpu = new FakeGpuDevice();
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(4096, GpuBufferUsage.Storage, new GpuObjectName("test", "cache"));
        var recorders = new Recorders("cache.update", Over);

        recorders.Registry.Register(factory: new BorrowedPackage(buffer: buffer) { OwnsAllocation = borrowed }, package: RenderGraphPackageCatalog.Indirect);
        var set = MutableBufferInstances(previous: previous);

        Assert.False(condition: RenderGraphRuntime.TryCreate(set, [MutableBufferGraph(), null], PackageView, recorders.Registry,
            new GpuPassPipelineCache(), gpu, false, out var runtime, out var refusal));
        Assert.Null(@object: runtime);
        Assert.Equal(RenderGraphRuntimeRefusalCode.MutableInput, refusal!.Code);
    }
    [Fact]
    public void AMutableInputCannotStandOnAnUnchangedPackageSignature() {
        var gpu = new FakePipelineGpu();
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(4096, GpuBufferUsage.Storage, new GpuObjectName("test", "cache"));
        var recorders = new Recorders("cache.update", Over);
        var producer = new BorrowedPackage(buffer: buffer);

        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        using var runtime = Runtime(gpu, recorders, MutableBufferInstances(), PackageView, MutableBufferGraph(), null!);
        var frame = 0L;

        TestLiveness.Until(() => { ProducePackageFrame(runtime, frame++); return (runtime.Node(instance: 0).FrameCounter > 2); });
        var counter = recorders.ByInstance[PackageView];

        counter.CadenceSignature = 1;
        producer.Unchanged = true;
        ProducePackageFrame(runtime, frame++);
        var recorded = counter.Records;

        for (var index = 0; (index < 3); index++) { ProducePackageFrame(runtime, frame++); }
        // Both the mutable writer and the real publication pass execute; a standing writer would record fewer.
        Assert.Equal(actual: counter.Records, expected: (recorded + 6));
        Assert.Equal(counter.PassRecords["update"].Output, counter.PassRecords["publish"].Input);
        Assert.NotEqual(counter.PassRecords["update"].Output, counter.PassRecords["publish"].Output);
    }
    [Fact]
    public void AReadOnlyConsumerCannotStandWhileAnotherConsumerMutatesAProducerAlias() {
        var gpu = new FakePipelineGpu();
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(PoolBytes, GpuBufferUsage.Storage, new GpuObjectName("test", "shared"));
        var producer = new NamedBufferPackage {
            Buffers = new Dictionary<string, IGpuBuffer>(comparer: StringComparer.Ordinal) {
                ["primary"] = buffer,
                ["secondary"] = buffer,
                ["private"] = buffer,
            },
            Signature = 1,
        };
        var recorders = new Recorders("cache.update", "cache.read", Over);
        var reader = new Counter { CadenceSignature = 1 };
        var writer = new Counter { CadenceSignature = 1 };

        recorders.ByInstance.Add(key: "main", value: reader);
        recorders.ByInstance.Add(key: "mutator", value: writer);
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        var set = Set(
            new RenderGraphInstance(Name: "main", Passes: 2, Reads: [new("cache", Kind: ShaderPipelineResourceKind.Buffer), new("mutator")], Refresh: RenderGraphRefresh.EveryFrame),
            new RenderGraphInstance(Name: "mutator", Passes: 2, Reads: [new("cache", Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new RenderGraphInstance(Name: "cache", ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 3, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        using var runtime = Runtime(gpu, recorders, set, "main",
            MutableBufferGraph(bytes: PoolBytes, mutable: false, output: "secondary"),
            MutableBufferGraph(output: "primary", bytes: PoolBytes), null!);
        var frames = new Frames(runtime, [new RenderGraphRoot(Height: 1, Instance: "main", Width: 1)], [
            new RenderGraphFootprint(Consumer: "main", Height: 1, Producer: "mutator", Width: 1),
        ]);

        runtime.Node(instance: 2).Paused = true;
        TestLiveness.Until(() => { frames.Next(); return (runtime.Node(instance: 0).FrameCounter > 1); });
        var reads = reader.Records;
        var writes = writer.Records;

        frames.Next(count: 3);
        Assert.Equal(actual: reader.Records, expected: (reads + 6));
        Assert.Equal(actual: writer.Records, expected: (writes + 6));

        // Re-resolving the set removes the mutable alias and permits its read-only consumers to stand again.
        Assert.True(condition: runtime.TryInstall("mutator", MutableBufferGraph(bytes: PoolBytes, mutable: false, output: "primary"), out var refusal), userMessage: refusal?.Message);
        TestLiveness.Until(() => { frames.Next(); return (!runtime.Node(instance: 1).HasPendingCandidate && runtime.Node(instance: 1).IsReady); });
        frames.Next(count: 2);
        reads = reader.Records;
        frames.Next(count: 3);
        Assert.Equal(actual: reader.Records, expected: (reads + 3));
    }
    [Fact]
    public void NamedBufferPublicationTracksItsOwnWritesBesideAForwardedImage() {
        var gpu = new FakePipelineGpu();
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(PoolBytes, GpuBufferUsage.Storage, new GpuObjectName("test", "published"));
        var producer = new PublicationBufferWriter(buffer: buffer);
        var recorders = new Recorders(Camera, "cache.read", Over);
        var reader = new Counter { CadenceSignature = 1 };
        var forwarder = new Counter { DrawsWhenRefused = true, Outcome = RenderGraphPackageOutcome.DrewNothing };

        recorders.ByInstance.Add(key: "main", value: reader);
        recorders.ByInstance.Add(key: "mixed", value: forwarder);
        recorders.Registry.Register(factory: producer, package: Pool);
        var mixed = Compile(definition: new RenderGraphDefinition(Name: "mixed", Schema: RenderGraphSchemas.Graph,
            Outputs: ["color", "shared"],
            Resources: [Image("world", external: true), Image("color"),
                new(Name: "shared", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: PoolBytes)],
            Packages: [new(Name: "fill", Package: Pool, Outputs: ["shared"]),
                new(Name: "forward", Package: Over, Inputs: ["world"], Outputs: ["color"])]));
        var read = MutableBufferGraph(bytes: PoolBytes, mutable: false, output: "shared") with {
            Inputs = [new(Output: "shared", Producer: "mixed", Version: "cache")],
        };
        var set = Set(Instance("world"),
            Instance("mixed", reads: new RenderGraphRead("world")) with { Passes = 2 },
            Instance("main", reads: new RenderGraphRead("mixed")) with { Passes = 2 });
        using var runtime = Runtime(gpu, recorders, set, "main", Graph(CameraGraph()), Graph(mixed, ("world", "world")), read);
        var frames = new Frames(runtime, [new RenderGraphRoot(Height: 1, Instance: "main", Width: 1)], [
            new RenderGraphFootprint(Consumer: "main", Height: 1, Producer: "mixed", Width: 1),
            new RenderGraphFootprint(Consumer: "mixed", Height: 1, Producer: "world", Width: 1),
        ]);

        runtime.Node(instance: 0).Paused = true;
        TestLiveness.Within(frames: 16, building: () => Enumerable.Range(count: 3, start: 0).Any(predicate: index => runtime.Node(instance: index).IsBuildingCandidate),
            reason: () => $"world={runtime.Node(instance: 0).FrameCounter}, mixed={runtime.Node(instance: 1).FrameCounter}, main={runtime.Node(instance: 2).FrameCounter}, forwarding={runtime.Node(instance: 1).PublishedBinding}, buffer writes={producer.Writes}, render={runtime.Render}",
            step: () => {
                frames.Next();
                return ((runtime.Node(instance: 2).FrameCounter > 1) && (runtime.Node(instance: 1).PublishedBinding == "world"));
            });
        var image = recorders.Publications["mixed"];
        var records = reader.Records;

        frames.Next(count: 3);
        Assert.Equal(expected: image, actual: recorders.Publications["mixed"]);
        Assert.Equal(actual: reader.Records, expected: (records + 6));

        // Only the image-forwarding pass keeps submitting once the buffer's own content signature stands.
        producer.Signature = 1;
        frames.Next(count: 2);
        records = reader.Records;
        var writes = producer.Writes;

        frames.Next(count: 3);
        Assert.Equal(actual: producer.Writes, expected: writes);
        Assert.Equal(actual: reader.Records, expected: (records + 3));
        Assert.Equal(expected: image, actual: recorders.Publications["mixed"]);
    }

    private sealed class PublicationBufferWriter(IGpuBuffer buffer) : IRenderGraphPackageFactory {
        public ulong? Signature;
        public int Writes;

        public bool OwnsBuffer(string? part) => true;
        public IGpuBuffer? BorrowedBuffer(RenderGraphPackageRecorderContext context, IDisposable? built, ShaderPipelineResource resource) => buffer;
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this);

        private sealed class Recorder(PublicationBufferWriter owner) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) => owner.Signature;
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) { owner.Writes++; return RenderGraphPackageOutcome.Drew; }
        }
    }

    private static RenderGraphInstanceSet MutableBufferInstances(bool previous = false) => Set(
        new RenderGraphInstance(Name: PackageView, Passes: 2, Reads: [new("cache", Kind: ShaderPipelineResourceKind.Buffer, PreviousFrame: previous)], Refresh: RenderGraphRefresh.EveryFrame),
        new RenderGraphInstance(Name: "cache", ExternalPackage: RenderGraphPackageCatalog.Indirect,
            Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
    private static RenderGraphRuntimeGraph MutableBufferGraph(bool mutable = true, string? output = null, ulong bytes = 4096) {
        var package = new RenderGraphPackage(Id: (mutable ? "cache.update" : "cache.read"), Members: [], Summary: "Accesses shared proofs.",
            Inputs: [RenderGraphPackagePort.Buffer(access: (mutable ? RenderGraphPortAccess.ComputeReadWrite : RenderGraphPortAccess.ComputeRead), count: null, strideBytes: null)],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)]);
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog(packages: [package, Catalog.Packages.Single(predicate: static item => (item.Id == Over))])).Compile(definition: new RenderGraphDefinition(
            Name: "update", Schema: RenderGraphSchemas.Graph, Outputs: ["color"],
            Resources: [Image("work") with { Retained = true }, Image("color"), new(Name: "cache", Kind: ShaderPipelineResourceKind.Buffer,
                SizeBytes: bytes, Initialization: ShaderPipelineInitialization.External)],
            Packages: [new(Name: "update", Package: package.Id, Inputs: ["cache"], Outputs: ["work"]),
                new(Name: "publish", Package: Over, Inputs: ["work"], Outputs: ["color"])]));

        return new(Pipeline: new CompiledShaderPipeline(plan: plan.Pipeline, shaders: new Dictionary<string, CompiledShader>()),
            Inputs: [new(Output: output, Producer: "cache", Version: "cache")]);
    }
}
