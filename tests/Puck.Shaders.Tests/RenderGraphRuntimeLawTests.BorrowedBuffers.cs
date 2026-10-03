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
        var creation = Assert.Single(gpu.Created);
        var recorders = new Recorders();
        var producer = new BorrowedPackage(buffer);
        var view = new BufferViewPackage(Bytes);

        recorders.Registry.Register(RenderGraphPackageCatalog.Indirect, producer);
        recorders.Registry.Register(RenderGraphPackageCatalog.SdfWorld, view);
        var set = Set(PackageInstance() with { Reads = [new RenderGraphRead("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 3, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));

        using (var runtime = Runtime(gpu, recorders, set, PackageView, new RenderGraphRuntimeGraph[2])) {
            var frame = 0L;

            TestLiveness.Until(step: () => {
                ProducePackageFrame(runtime, frame++);
                return (runtime.Node(0).FrameCounter > 2);
            });
            Assert.NotEmpty(producer.Outputs);
            Assert.All(producer.Outputs, output => Assert.Same(buffer, output));
            Assert.True((runtime.Node(1).AllocationBytes < Bytes));
            var rendered = runtime.Node(0).FrameCounter;
            var published = runtime.Node(1).FrameCounter;

            producer.Unchanged = true;
            view.Inner.Unchanged = true;
            for (var index = 0; (index < 6); index++) { ProducePackageFrame(runtime, frame++); }
            Assert.Equal(published, runtime.Node(1).FrameCounter);
            Assert.Equal(rendered, runtime.Node(0).FrameCounter);
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
        var producer = new BorrowedPackage(buffer) { Fragment = SdfWorldPackage.IndirectFragment(InitialBytes) };
        var view = new BufferViewPackage(InitialBytes);

        recorders.Registry.Register(RenderGraphPackageCatalog.Indirect, producer);
        recorders.Registry.Register(RenderGraphPackageCatalog.SdfWorld, view);
        var set = Set(PackageInstance() with { Reads = [new RenderGraphRead("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 3, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        using var runtime = Runtime(gpu, recorders, set, PackageView, new RenderGraphRuntimeGraph[2]);
        var frame = 0L;

        TestLiveness.Until(step: () => { ProducePackageFrame(runtime, frame++); return (runtime.Node(0).FrameCounter > 1); });
        var rendered = runtime.Node(0).FrameCounter;

        producer.Fragment = SdfWorldPackage.IndirectFragment(Bytes);
        view.Fragment = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, Bytes);
        TestLiveness.Until(step: () => { ProducePackageFrame(runtime, frame++); return (runtime.Node(0).FrameCounter > (rendered + 2)); });
        Assert.All(producer.Outputs, output => Assert.Same(buffer, output));
    }

    private sealed class BufferViewPackage(ulong bytes) : IRenderGraphPackageFactory {
        public ViewPackage Inner { get; } = new();
        public RenderGraphPackageFragment Fragment { get; set; } = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, bytes);

        public RenderGraphPackageFragment? FragmentOf(string instance) => Fragment;
        public IReadOnlyList<RenderGraphRuntimeInput> InputsOf(string instance) => [new(SdfWorldPackage.IndirectCache, "cache")];
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => Inner.BuildAsync(context, cancellationToken);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => Inner.Create(context, built, groups);
        public IShaderPipelineStorageCounter? CounterOf(string instance) => Inner;
        public bool IsUnchanged(string instance, long unreadFrames, in FrameContext context) => Inner.Unchanged;
    }
    private sealed class BorrowedPackage(IGpuBuffer buffer) : IRenderGraphPackageFactory {
        public RenderGraphPackageFragment Fragment { get; set; } = SdfWorldPackage.IndirectFragment(buffer.SizeBytes);

        public bool OwnsBuffers => true;
        public bool Unchanged { get; set; }

        public List<IGpuBuffer> Outputs { get; } = [];

        public RenderGraphPackageFragment? FragmentOf(string instance) => Fragment;
        public IGpuBuffer? BorrowedBuffer(RenderGraphPackageRecorderContext context, IDisposable? built, ShaderPipelineResource resource) => buffer;
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(this);
        public bool IsUnchanged(string instance, long unreadFrames, in FrameContext context) => Unchanged;

        private sealed class Recorder(BorrowedPackage owner) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Outputs.Add(recording.Outputs[0].Buffer!);
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
