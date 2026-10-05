using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void ABorrowedProducerReacquiresConsumerWritesEvenWhenItsFirstPassSkips() {
        const ulong Bytes = 1024 * 1024;
        var gpu = new FakePipelineGpu { Recording = true };
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(Bytes, GpuBufferUsage.Storage, new GpuObjectName("test", "cache"));
        var recorders = new Recorders();
        var producer = new BorrowedPackage(buffer);
        var view = new BufferViewPackage(Bytes);
        view.Fragment = view.Fragment with {
            Passes = [.. view.Fragment.Passes.Select(pass => (pass.Name == SdfWorldPackage.Parts.Views) ? pass with {
                InputAccesses = [.. pass.Inputs.Select((input, index) => (input.Name == SdfWorldPackage.IndirectCache)
                    ? RenderGraphPortAccess.ComputeReadWrite : pass.InputAccesses[index])],
            } : pass)],
        };
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        var set = Set(PackageInstance() with { Reads = [new("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        using var runtime = Runtime(gpu, recorders, set, PackageView, new RenderGraphRuntimeGraph[2]);
        var frame = 0L;
        TestLiveness.Until(() => { ProducePackageFrame(runtime, frame++); return runtime.Node(0).FrameCounter > 2; });
        producer.SkipPlacement = true;
        gpu.Barriers.Clear();
        ProducePackageFrame(runtime, frame++);
        var barriers = gpu.Barriers.Where(item => item.Handle == buffer.BufferHandle).Select(item => item.Barrier).ToArray();

        Assert.NotEmpty(barriers);
        Assert.True(barriers[0].SourceAccess.HasFlag(GpuAccess.ShaderWrite));
        Assert.True(barriers[0].SourceAccess.HasFlag(GpuAccess.TransferWrite));
        Assert.True(barriers[0].SourceStage.HasFlag(GpuStage.ComputeShader));
        Assert.True(barriers[0].SourceStage.HasFlag(GpuStage.Transfer));
        Assert.Contains(barriers, barrier => (barrier.DestinationAccess == (GpuAccess.ShaderRead | GpuAccess.ShaderWrite)));
        Assert.All(producer.Outputs, output => Assert.Same(buffer, output));
        Assert.True(runtime.Node(1).AllocationBytes < Bytes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AMutableEdgeRefusesHistoryAndGraphOwnedBufferRings(bool previous, bool borrowed) {
        var gpu = new FakeGpuDevice();
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(4096, GpuBufferUsage.Storage, new GpuObjectName("test", "cache"));
        var recorders = new Recorders("cache.update", Over);
        recorders.Registry.Register(factory: new BorrowedPackage(buffer) { OwnsAllocation = borrowed }, package: RenderGraphPackageCatalog.Indirect);
        var set = MutableBufferInstances(previous);
        Assert.False(RenderGraphRuntime.TryCreate(set, [MutableBufferGraph(), null], PackageView, recorders.Registry,
            new GpuPassPipelineCache(), gpu, false, out var runtime, out var refusal));
        Assert.Null(runtime);
        Assert.Equal(RenderGraphRuntimeRefusalCode.MutableInput, refusal!.Code);
    }

    [Fact]
    public void AMutableInputCannotStandOnAnUnchangedPackageSignature() {
        var gpu = new FakePipelineGpu();
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(4096, GpuBufferUsage.Storage, new GpuObjectName("test", "cache"));
        var recorders = new Recorders("cache.update", Over);
        var producer = new BorrowedPackage(buffer);
        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        using var runtime = Runtime(gpu, recorders, MutableBufferInstances(), PackageView, MutableBufferGraph(), null!);
        var frame = 0L;
        TestLiveness.Until(() => { ProducePackageFrame(runtime, frame++); return runtime.Node(0).FrameCounter > 2; });
        var counter = recorders.ByInstance[PackageView];
        counter.CadenceSignature = 1;
        producer.Unchanged = true;
        ProducePackageFrame(runtime, frame++);
        var recorded = counter.Records;
        for (var index = 0; index < 3; index++) { ProducePackageFrame(runtime, frame++); }
        // Both the mutable writer and the real publication pass execute; a standing writer would record fewer.
        Assert.Equal(recorded + 6, counter.Records);
        Assert.Equal(counter.PassRecords["update"].Output, counter.PassRecords["publish"].Input);
        Assert.NotEqual(counter.PassRecords["update"].Output, counter.PassRecords["publish"].Output);
    }

    private static RenderGraphInstanceSet MutableBufferInstances(bool previous = false) => Set(
        new RenderGraphInstance(Name: PackageView, Passes: 2, Reads: [new("cache", Kind: ShaderPipelineResourceKind.Buffer, PreviousFrame: previous)], Refresh: RenderGraphRefresh.EveryFrame),
        new RenderGraphInstance(Name: "cache", ExternalPackage: RenderGraphPackageCatalog.Indirect,
            Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));

    private static RenderGraphRuntimeGraph MutableBufferGraph() {
        var package = new RenderGraphPackage(Id: "cache.update", Members: [], Summary: "Updates shared proofs.",
            Inputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeReadWrite, null, null)],
            Outputs: [RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite)]);
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog([package, Catalog.Packages.Single(static item => item.Id == Over)])).Compile(new RenderGraphDefinition(
            Name: "update", Schema: RenderGraphSchemas.Graph, Outputs: ["color"],
            Resources: [Image("work") with { Retained = true }, Image("color"), new(Name: "cache", Kind: ShaderPipelineResourceKind.Buffer,
                SizeBytes: 4096, Initialization: ShaderPipelineInitialization.External)],
            Packages: [new(Name: "update", Package: "cache.update", Inputs: ["cache"], Outputs: ["work"]),
                new(Name: "publish", Package: Over, Inputs: ["work"], Outputs: ["color"])]));
        return Graph(new CompiledShaderPipeline(plan.Pipeline, new Dictionary<string, CompiledShader>()), ("cache", "cache"));
    }
}
