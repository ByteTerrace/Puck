using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// A graph instance's reads its graph binds to no version, as an SDF view's screens are: a package that samples them is
// handed their images in its recording, takes the lease of what it samples into the frame's lease list, which retires it
// after that frame's fence, and the runtime retires the rest once the frame is produced. A graph running no package that
// samples them acquires nothing it reads.
public sealed partial class RenderGraphRuntimeLawTests {
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AGraphInstanceSamplesTheReadsItsGraphBindsToNoVersion(bool samples) {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var source = new FakeSource(
            cadence: ImageSourceCadence.Tick,
            gpu: gpu
        );
        var viewing = new ReadingPackage(samples: samples);

        recorders.Registry.RegisterProducer(
            factory: _ => source,
            package: RenderGraphInstance.SourcePackage(producer: ReadProducer)
        );
        recorders.Registry.Register(
            factory: viewing,
            package: Camera
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(
                RenderGraphInstance.Source(
                    name: ReadSource,
                    producer: ReadProducer
                ),
                Instance(
                    name: "view",
                    reads: new RenderGraphRead(Producer: ReadSource)
                )
            ),
            "view",
            null!,
            Graph(pipeline: CameraGraph())
        );

        var index = 0L;

        // The view's graph builds on the thread pool, so frames are produced until it has recorded three; the third takes
        // its lease.
        TestLiveness.Until(
            reason: () => $"The view recorded {viewing.Seen.Count} frame(s).",
            step: () => {
                viewing.Takes = (viewing.Seen.Count == 2);
                Produce(frameIndex: index++);

                return (viewing.Seen.Count >= 3);
            }
        );

        void Produce(long frameIndex) {
            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: [new RenderGraphFootprint(Consumer: "view", Height: 0.25, Producer: ReadSource, Width: 0.25)],
                Index: frameIndex,
                Roots: [new RenderGraphRoot(Height: 1.0, Instance: "view", Width: 1.0)],
                Tick: frameIndex
            );

            _ = runtime.ProduceFrame(
                context: default,
                frame: in frame
            );
        }

        if (!samples) {
            // Nothing samples the read, so the source is never acquired and the recording is handed nothing.
            Assert.Equal(expected: 0, actual: source.Acquired);
            Assert.All(action: static seen => Assert.Null(@object: seen), collection: viewing.Seen);

            return;
        }

        Assert.All(action: seen => Assert.Equal(expected: source.ImageView, actual: seen), collection: viewing.Seen);
        // Every lease the view did not take is retired with its frame; the one it took waits for its frame's fence.
        Assert.Equal(expected: (source.Acquired - 1), actual: source.Released);

        runtime.Dispose();
        Assert.Equal(expected: source.Acquired, actual: source.Released);
    }
    [Fact]
    public void ACaptureWaitsForAScheduledUnboundScreenReadToHaveAnOutput() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var source = new FakeProducer(gpu: gpu) { Holding = true };

        recorders.Registry.RegisterProducer(factory: _ => source, package: World);
        recorders.Registry.Register(factory: new ReadingPackage(samples: true), package: Camera);
        using var runtime = Runtime(
            gpu,
            recorders,
            Set(
                new RenderGraphInstance(ExternalPackage: World, Name: ReadSource, Passes: 1, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
                Instance(name: "view", reads: new RenderGraphRead(Producer: ReadSource))
            ),
            "view",
            null!,
            Graph(pipeline: CameraGraph())
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: ReadSource, Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "view", Width: 1.0)],
            runtime: runtime
        );

        TestLiveness.Until(
            step: () => {
                _ = frames.Next();

                return (runtime.Node(instance: 1).FrameCounter > 0UL);
            }
        );

        var request = CaptureRequest();

        runtime.RequestCapture(request: request);
        _ = frames.Next();
        Assert.Equal(expected: request.Path, actual: runtime.PendingCapturePath);
        Assert.Contains(expectedSubstring: ReadSource, actualString: runtime.UnservedCaptureReason);

        source.Holding = false;
        _ = frames.Next();
        // The fake's unsupported readback refuses the forwarded request; it has reached the complete image.
        Assert.Null(@object: runtime.PendingCapturePath);
    }

    /// <summary>A camera package whose recorder samples its instance's unbound reads when it says so: it records the
    /// image view of the first read each frame and, when told to, takes its lease into the frame's lease list.</summary>
    private sealed class ReadingPackage(bool samples) : IRenderGraphPackageFactory {
        public List<nint?> Seen { get; } = [];
        public bool SamplesReads { get; } = samples;

        public bool Takes { get; set; }

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this);

        private sealed class Recorder(ReadingPackage owner) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                if (recording.Reads is not { } reads) {
                    owner.Seen.Add(item: null);

                    return RenderGraphPackageOutcome.Drew;
                }

                var index = reads.IndexOf(producer: ReadSource);

                if (reads[index].Image.IsSameDeviceImage) {
                    owner.Seen.Add(item: reads[index].Image.ImageViewHandle);

                    if (owner.Takes) {
                        var lease = reads.Take(index: index);

                        recording.Leases.Hold(lease: in lease);
                    }
                }

                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
