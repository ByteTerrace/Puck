using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void AnImplicitPackagePublishesOneBorrowedBufferAndItsReadersStandWithIt() {
        const ulong Bytes = (1024 * 1024);
        var gpu = new FakeGpuDevice(trackObjects: true);
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(Bytes, GpuBufferUsage.Storage, new GpuObjectName("test", "cache"));
        var creation = Assert.Single(collection: gpu.Created);
        var recorders = new Recorders();
        var producer = new BorrowedPackage(buffer: buffer);
        var view = new BufferViewPackage(bytes: Bytes);

        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        var set = Set(PackageInstance() with { Reads = [new RenderGraphRead("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));

        using (var runtime = Runtime(gpu, recorders, set, PackageView, new RenderGraphRuntimeGraph[2])) {
            var frame = 0L;

            TestLiveness.Until(step: () => {
                ProducePackageFrame(runtime, frame++);
                return (runtime.Node(instance: 0).FrameCounter > 2);
            });
            Assert.NotEmpty(collection: producer.Outputs);
            Assert.All(producer.Outputs, output => Assert.Same(actual: output, expected: buffer));
            Assert.True(condition: (runtime.Node(instance: 1).AllocationBytes < Bytes));
            var rendered = runtime.Node(instance: 0).FrameCounter;
            var published = runtime.Node(instance: 1).FrameCounter;

            producer.Unchanged = true;
            view.Inner.Unchanged = true;
            for (var index = 0; (index < 6); index++) { ProducePackageFrame(runtime, frame++); }
            Assert.Equal(published, runtime.Node(instance: 1).FrameCounter);
            Assert.Equal(rendered, runtime.Node(instance: 0).FrameCounter);
        }
        Assert.Equal(0, creation.DisposeCount);
    }
    [Fact]
    public void PackageBufferExtentsRebindAfterBothProducerAndReaderFragmentsChange() {
        const ulong InitialBytes = 4096;
        const ulong Bytes = 8192;
        var gpu = new FakeGpuDevice();
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(Bytes, GpuBufferUsage.Storage, new GpuObjectName("test", "cache"));
        var recorders = new Recorders();
        var producer = new BorrowedPackage(buffer: buffer) { Fragment = SdfWorldPackage.IndirectFragment(bytes: InitialBytes) };
        var view = new BufferViewPackage(bytes: InitialBytes);

        recorders.Registry.Register(factory: producer, package: RenderGraphPackageCatalog.Indirect);
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        var set = Set(PackageInstance() with { Reads = [new RenderGraphRead("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        using var runtime = Runtime(gpu, recorders, set, PackageView, new RenderGraphRuntimeGraph[2]);
        var frame = 0L;

        TestLiveness.Until(step: () => { ProducePackageFrame(runtime, frame++); return (runtime.Node(instance: 0).FrameCounter > 1); });
        var rendered = runtime.Node(instance: 0).FrameCounter;

        producer.Fragment = SdfWorldPackage.IndirectFragment(bytes: Bytes);
        view.Fragment = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, Bytes);
        TestLiveness.Until(step: () => { ProducePackageFrame(runtime, frame++); return (runtime.Node(instance: 0).FrameCounter > (rendered + 2)); });
        Assert.All(producer.Outputs, output => Assert.Same(actual: output, expected: buffer));
    }

    private sealed class BufferViewPackage(ulong bytes) : IRenderGraphPackageFactory {
        public ViewPackage Inner { get; } = new();
        public RenderGraphPackageFragment Fragment { get; set; } = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, bytes);

        public RenderGraphPackageFragment? FragmentOf(string instance) => Fragment;
        public IReadOnlyList<RenderGraphRuntimeInput> InputsOf(string instance) => [new(Producer: "cache", Version: SdfWorldPackage.IndirectCache)];
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => Inner.BuildAsync(cancellationToken: cancellationToken, context: context);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => Inner.Create(built: built, context: context, groups: groups);
        public IShaderPipelineStorageCounter? CounterOf(string instance) => Inner;
        public bool IsUnchanged(string instance, long unreadFrames, in FrameContext context) => Inner.Unchanged;
    }
    private sealed class BorrowedPackage(IGpuBuffer buffer) : IRenderGraphPackageFactory {
        public RenderGraphPackageFragment Fragment { get; set; } = SdfWorldPackage.IndirectFragment(bytes: buffer.SizeBytes);

        public bool OwnsAllocation { get; set; } = true;
        public bool OwnsBuffer(string? part) => OwnsAllocation;
        public bool Unchanged { get; set; }
        public bool SkipPlacement { get; set; }

        public List<IGpuBuffer> Outputs { get; } = [];

        public RenderGraphPackageFragment? FragmentOf(string instance) => Fragment;
        public IGpuBuffer? BorrowedBuffer(RenderGraphPackageRecorderContext context, IDisposable? built, ShaderPipelineResource resource) => buffer;
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this, part: context.Part);
        public bool IsUnchanged(string instance, long unreadFrames, in FrameContext context) => Unchanged;

        private sealed class Recorder(BorrowedPackage owner, string? part) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public bool Skips(in FrameContext context) => (owner.SkipPlacement && (part == SdfWorldPackage.IndirectPlace));
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Outputs.Add(item: recording.Outputs[0].Buffer!);
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
