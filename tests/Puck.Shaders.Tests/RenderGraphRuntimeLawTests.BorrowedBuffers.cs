using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void BorrowedBackingChangesWithoutRebuildingAndKeepsThePreviousOwnerUntilReadersFinish() {
        const ulong Bytes = 4096;
        var gpu = new FakePipelineGpu();
        var first = new SharedBufferOwner(gpu.Services.BufferFactory.CreateDeviceLocal(Bytes, GpuBufferUsage.Storage, new("test", "first")));
        var second = new SharedBufferOwner(gpu.Services.BufferFactory.CreateDeviceLocal(Bytes, GpuBufferUsage.Storage, new("test", "second")));
        var producer = new BorrowedPackage(first.Buffer) { LiveOwner = first };
        var recorders = new Recorders();
        recorders.Registry.Register(factory: producer, package: BufferPackage);
        recorders.Registry.Register(factory: new BufferViewPackage(Bytes), package: RenderGraphPackageCatalog.SdfWorld);
        var set = Set(PackageInstance() with { Reads = [new("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: BufferPackage, Output: ShaderPipelineResourceKind.Buffer,
                Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        using (var runtime = Runtime(gpu, recorders, set, PackageView, new RenderGraphRuntimeGraph[2])) {
            var frame = 0L;
            TestLiveness.Until(() => { ProducePackageFrame(runtime, frame++); return runtime.Node(instance: 0).FrameCounter > 2; });
            var builds = producer.Builds;
            gpu.QueueHeld = true;
            producer.LiveOwner = second;
            first.Dispose();
            producer.Outputs.Clear();
            ProducePackageFrame(runtime, frame++);
            Assert.NotEmpty(producer.Outputs);
            Assert.All(producer.Outputs, output => Assert.Same(second.Buffer, output));
            Assert.True(first.References > 0);
            Assert.Equal(builds, producer.Builds);
            gpu.QueueHeld = false;
            TestLiveness.Until(() => { ProducePackageFrame(runtime, frame++); return first.References == 0; });
            second.Dispose();
            Assert.True(second.References > 0);
        }
        Assert.Equal(0, second.References);
        Assert.Empty(gpu.UsesAfterRelease);
    }

    [Fact]
    public void AReadbackCopiesBytesFromTheNewBorrowedBackingAfterASwitch() {
        const ulong Bytes = 4096;
        var gpu = new UploadModelGpu();
        using var first = gpu.Services.BufferFactory.CreateDeviceLocal(Bytes, GpuBufferUsage.Storage, new("test", "first"));
        using var second = gpu.Services.BufferFactory.CreateDeviceLocal(Bytes, GpuBufferUsage.Storage, new("test", "second"));
        using var answer = gpu.Services.BufferFactory.CreateReadback(32, new("test", "answer"));
        var firstBytes = gpu.DeviceLocal(Bytes, "first");
        var secondBytes = gpu.DeviceLocal(Bytes, "second");
        for (var index = 0; index < firstBytes.Length; index++) { firstBytes[index] = (byte)index; secondBytes[index] = (byte)(index ^ 0xa5); }
        var producer = new BorrowedPackage(first) { CurrentBuffer = first, Readback = answer };
        var recorders = new Recorders();
        recorders.Registry.Register(factory: producer, package: BufferPackage);
        recorders.Registry.Register(factory: new BufferViewPackage(Bytes), package: RenderGraphPackageCatalog.SdfWorld);
        var set = Set(PackageInstance() with { Reads = [new("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: BufferPackage, Output: ShaderPipelineResourceKind.Buffer,
                Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        using var runtime = Runtime(gpu, recorders, set, PackageView, new RenderGraphRuntimeGraph[2]);
        var frame = 0L;
        TestLiveness.Until(() => { ProducePackageFrame(runtime, frame++); return (producer.Answers.Count > 0); });
        Assert.Equal(firstBytes.AsSpan(12, 20).ToArray(), producer.Answers[^1].AsSpan(4, 20).ToArray());
        producer.CurrentBuffer = second;
        var expected = secondBytes.AsSpan(12, 20).ToArray();
        TestLiveness.Until(() => { ProducePackageFrame(runtime, frame++); return producer.Answers[^1].AsSpan(4, 20).SequenceEqual(expected); });
    }

    [Fact]
    public void AnImplicitPackagePublishesOneBorrowedBufferAndItsReadersStandWithIt() {
        const ulong Bytes = (1024 * 1024);
        var gpu = new FakeGpuDevice(trackObjects: true);
        using var buffer = gpu.Services.BufferFactory.CreateDeviceLocal(Bytes, GpuBufferUsage.Storage, new GpuObjectName("test", "cache"));
        var creation = Assert.Single(collection: gpu.Created);
        var recorders = new Recorders();
        var producer = new BorrowedPackage(buffer: buffer);
        var view = new BufferViewPackage(bytes: Bytes);

        recorders.Registry.Register(factory: producer, package: BufferPackage);
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        var set = Set(PackageInstance() with { Reads = [new RenderGraphRead("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: BufferPackage,
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
        var producer = new BorrowedPackage(buffer: buffer) { Fragment = ProducerFragment(bytes: InitialBytes) };
        var view = new BufferViewPackage(bytes: InitialBytes);

        recorders.Registry.Register(factory: producer, package: BufferPackage);
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        var set = Set(PackageInstance() with { Reads = [new RenderGraphRead("cache", Kind: ShaderPipelineResourceKind.Buffer)] },
            new RenderGraphInstance(Name: "cache", ExternalPackage: BufferPackage,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        using var runtime = Runtime(gpu, recorders, set, PackageView, new RenderGraphRuntimeGraph[2]);
        var frame = 0L;

        TestLiveness.Until(step: () => { ProducePackageFrame(runtime, frame++); return (runtime.Node(instance: 0).FrameCounter > 1); });
        var rendered = runtime.Node(instance: 0).FrameCounter;

        producer.Fragment = ProducerFragment(bytes: Bytes);
        view.Fragment = ReaderFragment(SdfWorldPackage.NativeFragment, Bytes);
        TestLiveness.Until(step: () => { ProducePackageFrame(runtime, frame++); return (runtime.Node(instance: 0).FrameCounter > (rendered + 2)); });
        Assert.All(producer.Outputs, output => Assert.Same(actual: output, expected: buffer));
    }

    private sealed class BufferViewPackage(ulong bytes) : IRenderGraphPackageFactory {
        public ViewPackage Inner { get; } = new();
        public RenderGraphPackageFragment Fragment { get; set; } = ReaderFragment(SdfWorldPackage.NativeFragment, bytes);

        public RenderGraphPackageFragment? FragmentOf(string instance) => Fragment;
        public IReadOnlyList<RenderGraphRuntimeInput> InputsOf(string instance) => [new(Producer: "cache", Version: CacheVersion)];
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => Inner.BuildAsync(cancellationToken: cancellationToken, context: context);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => Inner.Create(built: built, context: context, groups: groups);
        public IShaderPipelineStorageCounter? CounterOf(string instance) => Inner;
        public bool IsUnchanged(string instance, long unreadFrames, in FrameContext context) => Inner.Unchanged;
    }
    private sealed class BorrowedPackage(IGpuBuffer buffer) : IRenderGraphPackageFactory {
        public SharedBufferOwner? LiveOwner { get; set; }
        public IGpuBuffer? CurrentBuffer { get; set; }
        public IGpuReadbackBuffer? Readback { get; set; }
        public List<byte[]> Answers { get; } = [];
        public int Builds => Volatile.Read(ref m_builds);
        public RenderGraphPackageFragment Fragment { get; set; } = ProducerFragment(bytes: buffer.SizeBytes);
        public bool OwnsAllocation { get; set; } = true;

        public bool OwnsBuffer(string? part) => OwnsAllocation;

        public bool SkipPlacement { get; set; }
        public bool Unchanged { get; set; }

        public List<IGpuBuffer> Outputs { get; } = [];

        public RenderGraphPackageFragment? FragmentOf(string instance) => Fragment;
        public RenderGraphBorrowedBuffer? BorrowedBuffer(RenderGraphPackageRecorderContext context, IDisposable? built, ShaderPipelineResource resource, IGpuBuffer? previous) {
            var selected = (LiveOwner?.Buffer ?? CurrentBuffer ?? buffer);
            return new(selected, ReferenceEquals(selected, previous) ? null : LiveOwner?.Retain());
        }
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
            Interlocked.Increment(ref m_builds);
            return ValueTask.FromResult<IDisposable?>(result: null);
        }
        private int m_builds;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this, part: context.Part);
        public bool IsUnchanged(string instance, long unreadFrames, in FrameContext context) => Unchanged;

        private sealed class Recorder(BorrowedPackage owner, string? part) : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
            private string? m_version;
            public ulong ReadbackBytes => ((part == "shade") ? (owner.Readback?.SizeBytes ?? 0) : 0);
            public void Dispose() { }
            public bool Skips(in FrameContext context) => (owner.SkipPlacement && (part == PlacePass));
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Outputs.Add(item: recording.Outputs[0].Buffer!);
                m_version = recording.Outputs[0].Version;
                return RenderGraphPackageOutcome.Drew;
            }
            public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) {
                if ((part == "shade") && (index == 0) && (owner.Readback is { } answer) && (m_version is { } version)) {
                    readback = new(version, 12, 20, answer, 4);
                    return true;
                }
                readback = default;
                return false;
            }
            private readonly HashSet<int> m_submitted = [];
            public void Submitted(int slot) {
                if ((part == "shade") && (owner.Readback is not null)) { m_submitted.Add(slot); }
            }
            // A copy is read only once the node proves its slot complete.
            public void Completed(int slot) {
                if (!m_submitted.Remove(slot)) { return; }
                var bytes = new byte[32];
                owner.Readback!.Read(bytes);
                owner.Answers.Add(bytes);
            }
        }
    }
}
