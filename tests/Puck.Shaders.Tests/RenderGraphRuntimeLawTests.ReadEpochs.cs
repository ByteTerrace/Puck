using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AFiniteReadEpochCopiesOnceForAllConsumersAcrossLaterSourcePublications(bool tainted) {
        var gpu = new FakePipelineGpu { Recording = true, TracksPendingReads = true };
        var source = new EpochSource(inner: new FakeSource(gpu, ImageSourceCadence.Tick)) { Tainted = tainted };
        var packages = new EpochReaders();
        var recorders = new Recorders();

        recorders.Registry.RegisterProducer(RenderGraphInstance.SourcePackage(producer: ReadProducer), _ => source);
        recorders.Registry.Register(factory: packages, package: Camera);
        using var runtime = Runtime(gpu, recorders,
            Set(RenderGraphInstance.Source(ReadSource, ReadProducer),
                Instance("left", reads: new RenderGraphRead(ReadSource)), Instance("right", reads: new RenderGraphRead(ReadSource))),
            "left", null!, Graph(CameraGraph()), Graph(CameraGraph()));
        var index = 0L;

        void Next() {
            var frame = new RenderGraphFrame(DisplayHeight: Display, DisplayHertz: 60, DisplayWidth: Display,
                Index: index, Tick: index++,
                Roots: [new(Height: 1, Instance: "left", Width: 1), new(Height: 1, Instance: "right", Width: 1)],
                Footprints: [new(Consumer: "left", Height: 1, Producer: ReadSource, Width: 1),
                    new(Consumer: "right", Height: 1, Producer: ReadSource, Width: 1)]);

            _ = runtime.ProduceFrame(context: default, frame: frame);
        }
        TestLiveness.Until(step: () => {
            Next();
            return (runtime.IsSettled && (packages.Seen.Count == 2));
        });
        var left = runtime.NodeOf(instance: "left")!;
        var right = runtime.NodeOf(instance: "right")!;
        var ordinaryBytes = (left.OwnedBytes + right.OwnedBytes);
        using var epoch = new RenderGraphReadEpoch();

        packages.Epoch = epoch;
        Next();

        var frozen = packages.Seen["left"];
        var copy = Assert.Single(collection: gpu.CopiedImages);

        Assert.NotEqual(actual: copy.Destination, expected: copy.Source);
        Assert.Equal(copy.Destination, frozen.Image.ImageHandle);
        Assert.Equal(frozen, packages.Seen["right"]);
        Assert.Same(source.Inner, frozen.Publication.Owner);
        Assert.Equal(actual: frozen.Tainted, expected: tainted);
        Assert.Equal((ordinaryBytes + ((FakeSource.Width * FakeSource.Height) * 4UL)), (left.OwnedBytes + right.OwnedBytes));

        source.Tainted = false;
        for (var frame = 0; (frame < 12); frame++) {
            Next();
            Assert.Equal(frozen, packages.Seen["left"]);
            Assert.Equal(frozen, packages.Seen["right"]);
        }
        Assert.True(condition: (source.Inner.Produced > (frozen.Publication.Sequence + 3)));
        Assert.True(condition: epoch.Changed);
        Assert.Single(collection: gpu.CopiedImages);
        Assert.False(condition: gpu.IsReleased(handle: copy.Destination));
        // The original ring slot has no epoch-long reader; only the owned copy remains in use.
        Assert.Equal(source.Inner.Acquired, source.Inner.Released);

        source.Available = false;
        Next();
        Assert.Equal(frozen, packages.Seen["left"]);
        Assert.Equal(frozen, packages.Seen["right"]);

        source.Available = true;
        epoch.Dispose();
        for (var frame = 0; (frame < 12); frame++) { Next(); }
        Assert.Equal(source.Inner.ImageView, packages.Seen["left"].Image.ImageViewHandle);
        Assert.True(condition: gpu.IsReleased(handle: copy.Destination));
        Assert.Equal(ordinaryBytes, (left.OwnedBytes + right.OwnedBytes));

        using var nextEpoch = new RenderGraphReadEpoch();

        packages.Epoch = nextEpoch;
        Next();
        var replacement = packages.Seen["left"];

        Assert.NotEqual(frozen.Image.ImageHandle, replacement.Image.ImageHandle);
        Assert.True(condition: (replacement.Publication.Sequence > frozen.Publication.Sequence));
        Assert.False(condition: replacement.Tainted);
        Assert.Equal(replacement, packages.Seen["right"]);
        Assert.Equal(2, gpu.CopiedImages.Count);
        runtime.Dispose();
        Assert.False(condition: nextEpoch.IsValid);
        Assert.Equal(source.Inner.Acquired, source.Inner.Released);
        Assert.Empty(collection: gpu.UsesAfterRelease);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AFiniteReadEpochRefusesAnAcquisitionWithoutItsCopyableImageOrPublication(bool viewOnly) {
        var gpu = new FakePipelineGpu();
        var source = new EpochSource(inner: new FakeSource(gpu, ImageSourceCadence.Tick) { ViewOnly = viewOnly }) {
            Publishes = viewOnly,
        };
        using var epoch = new RenderGraphReadEpoch();
        var packages = new EpochReaders { Epoch = epoch };
        var recorders = new Recorders();

        recorders.Registry.RegisterProducer(RenderGraphInstance.SourcePackage(producer: ReadProducer), _ => source);
        recorders.Registry.Register(factory: packages, package: Camera);
        using var runtime = Runtime(gpu, recorders,
            Set(RenderGraphInstance.Source(ReadSource, ReadProducer), Instance("view", reads: new RenderGraphRead(ReadSource))),
            "view", null!, Graph(CameraGraph()));
        var frames = new Frames(runtime, [new(Height: 1, Instance: "view", Width: 1)],
            [new(Consumer: "view", Height: 1, Producer: ReadSource, Width: 1)]);

        _ = frames.Next();
        Assert.Equal(FrameCompletion.Refused, runtime.Render.Completion);
        Assert.Contains("complete acquired image and publication", runtime.Render.Reason);
        Assert.Empty(collection: packages.Seen);
        Assert.Empty(collection: gpu.CopiedImages);
        Assert.Equal(source.Inner.Acquired, source.Inner.Released);
    }

    private sealed class EpochReaders : IRenderGraphPackageFactory {
        public RenderGraphReadEpoch? Epoch { get; set; }
        public bool SamplesReads => true;
        public Dictionary<string, (Surface Image, GpuImagePublication Publication, bool Tainted)> Seen { get; } = [];

        public RenderGraphReadEpoch? ReadEpochOf(string instance, string producer) => Epoch;
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) =>
            new Recorder(this, context.Instance);

        private sealed class Recorder(EpochReaders owner, string instance) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                var reads = recording.Reads!;
                var input = reads[0];

                owner.Seen[instance] = (input.Image, input.Publication, input.Tainted);
                recording.Leases.Hold(lease: reads.Take(index: 0));
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
    private sealed class EpochSource(FakeSource inner) : IRenderGraphSourceProducer {
        public FakeSource Inner { get; } = inner;
        public bool Available { get; set; } = true;
        public bool Publishes { get; init; } = true;

        public FrameRender Answer => Inner.Answer;
        public ImageSourceDescriptor? Descriptor => Inner.Descriptor;
        public GpuPixelFormat Format => Inner.Format;
        public string? NotReadyReason => Inner.NotReadyReason;
        public string? PendingCapturePath => Inner.PendingCapturePath;
        public bool Tainted { get; set; }
        public IGpuWorkSource Work => Inner.Work;

        public void Dispose() => Inner.Dispose();
        public void OnDeviceLost() => Inner.OnDeviceLost();
        public void RequestCapture(FrameCaptureRequest request) => Inner.RequestCapture(request: request);
        public FrameRender Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) =>
            Inner.Produce(context: context, height: height, reads: reads, width: width);
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            output = default;
            if (!Available || !Inner.TryAcquireOutput(output: out output)) { return false; }
            output = output with { Tainted = Tainted, Lease = output.Lease with { Publication = (Publishes ? output.Lease.Publication : default) } };
            return true;
        }
    }
}
