using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFiniteReadEpochCopiesOnceForAllConsumersAcrossLaterSourcePublications(bool tainted) {
        var gpu = new FakePipelineGpu { Recording = true, TracksPendingReads = true };
        var source = new EpochSource(new FakeSource(gpu, ImageSourceCadence.Tick)) { Tainted = tainted };
        var packages = new EpochReaders();
        var recorders = new Recorders();
        recorders.Registry.RegisterProducer(RenderGraphInstance.SourcePackage(ReadProducer), _ => source);
        recorders.Registry.Register(Camera, packages);
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
            _ = runtime.ProduceFrame(frame, default);
        }
        TestLiveness.Until(step: () => {
            Next();
            return runtime.IsSettled && packages.Seen.Count == 2;
        });
        var left = runtime.NodeOf("left")!;
        var right = runtime.NodeOf("right")!;
        var ordinaryBytes = left.OwnedBytes + right.OwnedBytes;
        using var epoch = new RenderGraphReadEpoch();
        packages.Epoch = epoch;
        Next();

        var frozen = packages.Seen["left"];
        var copy = Assert.Single(gpu.CopiedImages);
        Assert.NotEqual(copy.Source, copy.Destination);
        Assert.Equal(copy.Destination, frozen.Image.ImageHandle);
        Assert.Equal(frozen, packages.Seen["right"]);
        Assert.Same(source.Inner, frozen.Publication.Owner);
        Assert.Equal(tainted, frozen.Tainted);
        Assert.Equal(ordinaryBytes + FakeSource.Width * FakeSource.Height * 4UL, left.OwnedBytes + right.OwnedBytes);

        source.Tainted = false;
        for (var frame = 0; frame < 12; frame++) {
            Next();
            Assert.Equal(frozen, packages.Seen["left"]);
            Assert.Equal(frozen, packages.Seen["right"]);
        }
        Assert.True(source.Inner.Produced > frozen.Publication.Sequence + 3);
        Assert.True(epoch.Changed);
        Assert.Single(gpu.CopiedImages);
        Assert.False(gpu.IsReleased(copy.Destination));
        // The original ring slot has no epoch-long reader; only the owned copy remains in use.
        Assert.Equal(source.Inner.Acquired, source.Inner.Released);

        source.Available = false;
        Next();
        Assert.Equal(frozen, packages.Seen["left"]);
        Assert.Equal(frozen, packages.Seen["right"]);

        source.Available = true;
        epoch.Dispose();
        for (var frame = 0; frame < 12; frame++) { Next(); }
        Assert.Equal(source.Inner.ImageView, packages.Seen["left"].Image.ImageViewHandle);
        Assert.True(gpu.IsReleased(copy.Destination));
        Assert.Equal(ordinaryBytes, left.OwnedBytes + right.OwnedBytes);

        using var nextEpoch = new RenderGraphReadEpoch();
        packages.Epoch = nextEpoch;
        Next();
        var replacement = packages.Seen["left"];
        Assert.NotEqual(frozen.Image.ImageHandle, replacement.Image.ImageHandle);
        Assert.True(replacement.Publication.Sequence > frozen.Publication.Sequence);
        Assert.False(replacement.Tainted);
        Assert.Equal(replacement, packages.Seen["right"]);
        Assert.Equal(2, gpu.CopiedImages.Count);
        runtime.Dispose();
        Assert.False(nextEpoch.IsValid);
        Assert.Equal(source.Inner.Acquired, source.Inner.Released);
        Assert.Empty(gpu.UsesAfterRelease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFiniteReadEpochRefusesAnAcquisitionWithoutItsCopyableImageOrPublication(bool viewOnly) {
        var gpu = new FakePipelineGpu();
        var source = new EpochSource(new FakeSource(gpu, ImageSourceCadence.Tick) { ViewOnly = viewOnly }) {
            Publishes = viewOnly,
        };
        using var epoch = new RenderGraphReadEpoch();
        var packages = new EpochReaders { Epoch = epoch };
        var recorders = new Recorders();
        recorders.Registry.RegisterProducer(RenderGraphInstance.SourcePackage(ReadProducer), _ => source);
        recorders.Registry.Register(Camera, packages);
        using var runtime = Runtime(gpu, recorders,
            Set(RenderGraphInstance.Source(ReadSource, ReadProducer), Instance("view", reads: new RenderGraphRead(ReadSource))),
            "view", null!, Graph(CameraGraph()));
        var frames = new Frames(runtime, [new(Height: 1, Instance: "view", Width: 1)],
            [new(Consumer: "view", Height: 1, Producer: ReadSource, Width: 1)]);
        _ = frames.Next();
        Assert.Equal(FrameCompletion.Refused, runtime.Render.Completion);
        Assert.Contains("complete acquired image and publication", runtime.Render.Reason);
        Assert.Empty(packages.Seen);
        Assert.Empty(gpu.CopiedImages);
        Assert.Equal(source.Inner.Acquired, source.Inner.Released);
    }

    private sealed class EpochReaders : IRenderGraphPackageFactory {
        public RenderGraphReadEpoch? Epoch { get; set; }
        public Dictionary<string, (Surface Image, GpuImagePublication Publication, bool Tainted)> Seen { get; } = [];
        public bool SamplesReads => true;
        public RenderGraphReadEpoch? ReadEpochOf(string instance, string producer) => Epoch;
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IDisposable?>(null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) =>
            new Recorder(this, context.Instance);

        private sealed class Recorder(EpochReaders owner, string instance) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                var reads = recording.Reads!;
                var input = reads[0];
                owner.Seen[instance] = (input.Image, input.Publication, input.Tainted);
                recording.Leases.Hold(reads.Take(0));
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }

    private sealed class EpochSource(FakeSource inner) : IRenderGraphSourceProducer {
        public FakeSource Inner { get; } = inner;
        public bool Available { get; set; } = true;
        public bool Publishes { get; init; } = true;
        public bool Tainted { get; set; }
        public ImageSourceDescriptor? Descriptor => Inner.Descriptor;
        public FrameRender Answer => Inner.Answer;
        public GpuPixelFormat Format => Inner.Format;
        public string? NotReadyReason => Inner.NotReadyReason;
        public string? PendingCapturePath => Inner.PendingCapturePath;
        public IGpuWorkSource Work => Inner.Work;
        public void Dispose() => Inner.Dispose();
        public void OnDeviceLost() => Inner.OnDeviceLost();
        public void RequestCapture(FrameCaptureRequest request) => Inner.RequestCapture(request);
        public FrameRender Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) =>
            Inner.Produce(context, width, height, reads);
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            output = default;
            if (!Available || !Inner.TryAcquireOutput(out output)) { return false; }
            output = output with { Tainted = Tainted, Lease = output.Lease with { Publication = Publishes ? output.Lease.Publication : default } };
            return true;
        }
    }
}
