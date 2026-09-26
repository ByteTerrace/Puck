using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// External producers that read: a world producer reading a source instance's image, handed to it as a lease when it
// produces, and a source producer scheduled at the cadence and extent its descriptor declares.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string ReadProducer = "camera";
    private const string ReadSource = "source$camera$0";

    // A source producer and the world producer reading it, the world the root.
    private static (RenderGraphRuntime Runtime, FakeSource Source, ReadingWorld World) ReadingScene(FakePipelineGpu gpu, ImageSourceCadence cadence) {
        var recorders = new Recorders();
        var source = new FakeSource(
            cadence: cadence,
            gpu: gpu
        );
        var world = new ReadingWorld();

        recorders.Registry.RegisterProducer(
            factory: _ => source,
            package: RenderGraphInstance.SourcePackage(producer: ReadProducer)
        );
        recorders.Registry.RegisterProducer(
            factory: _ => world,
            package: World
        );

        var runtime = Runtime(
            gpu,
            recorders,
            Set(
                RenderGraphInstance.Source(
                    name: ReadSource,
                    producer: ReadProducer
                ),
                new RenderGraphInstance(
                    ExternalPackage: World,
                    Name: "world",
                    Passes: WorldPasses,
                    Reads: [new RenderGraphRead(Producer: ReadSource)],
                    Refresh: RenderGraphRefresh.EveryFrame
                )
            ),
            "world",
            null!,
            null!
        );

        return (runtime, source, world);
    }
    private static RenderGraphSchedule ProduceReading(RenderGraphRuntime runtime, long index, long tick, int hertz) {
        var frame = new RenderGraphFrame(
            DisplayHeight: Display,
            DisplayHertz: hertz,
            DisplayWidth: Display,
            Footprints: [new RenderGraphFootprint(Consumer: "world", Height: 0.25, Producer: ReadSource, Width: 0.25)],
            Index: index,
            Roots: [new RenderGraphRoot(Height: 1.0, Instance: "world", Width: 1.0)],
            Tick: tick
        );

        _ = runtime.ProduceFrame(
            context: default,
            frame: in frame
        );

        return runtime.Latest!;
    }

    [Fact]
    public void AnExternalProducerReadsItsSourcesLatestOutputAsALease() {
        var gpu = new FakePipelineGpu();

        var (runtime, source, world) = ReadingScene(
            cadence: ImageSourceCadence.Tick,
            gpu: gpu
        );

        using (runtime) {
            // The source renders before the world that reads it, and the world is handed its image.
            _ = ProduceReading(
                hertz: 60,
                index: 0,
                runtime: runtime,
                tick: 1
            );
            Assert.Equal(expected: 1, actual: source.Produced);
            Assert.Equal(expected: [(ReadSource, source.ImageView)], actual: world.Seen);
            // A lease the world did not take is retired once it has produced.
            Assert.Equal(expected: (1, 1), actual: (source.Acquired, source.Released));

            // A taken lease is the world's to retire, after the submission that sampled it.
            world.Takes = true;
            _ = ProduceReading(
                hertz: 60,
                index: 1,
                runtime: runtime,
                tick: 1
            );
            Assert.Equal(expected: (2, 1), actual: (source.Acquired, source.Released));

            world.Held.RetireAll();
            Assert.Equal(expected: (2, 2), actual: (source.Acquired, source.Released));
        }
    }
    [Fact]
    public void ASourceProducerRendersAtItsDeclaredCadenceAndExtent() {
        var gpu = new FakePipelineGpu();

        var (runtime, source, _) = ReadingScene(
            cadence: ImageSourceCadence.Tick,
            gpu: gpu
        );

        using (runtime) {
            for (var frame = 0; (frame < 6); frame++) {
                _ = ProduceReading(
                    hertz: 60,
                    index: frame,
                    runtime: runtime,
                    tick: (frame / 2)
                );
            }

            // Three ticks over six frames: one render per completed tick, at the negotiated extent, never the footprint's.
            Assert.Equal(expected: 3, actual: source.Produced);
            Assert.Equal(expected: (FakeSource.Width, FakeSource.Height), actual: source.Extent);
        }
    }
    [Fact]
    public void ARateSourceIsRefusedByNameWhileTheDisplayRateIsUnknown() {
        var gpu = new FakePipelineGpu();

        var (runtime, source, _) = ReadingScene(
            cadence: ImageSourceCadence.Rate(rateHz: 30),
            gpu: gpu
        );

        using (runtime) {
            for (var frame = 0; (frame < 4); frame++) {
                var schedule = ProduceReading(
                    hertz: 0,
                    index: frame,
                    runtime: runtime,
                    tick: frame
                );

                Assert.Equal(
                    actual: schedule.Instances[runtime.Instances.IndexOf(name: ReadSource)].Status,
                    expected: RenderGraphInstanceStatus.Refused
                );
            }

            Assert.Equal(expected: 0, actual: source.Produced);

            // Once the display's rate is known, a 30 Hz source renders every second frame of a 60 Hz display.
            for (var frame = 4; (frame < 8); frame++) {
                _ = ProduceReading(
                    hertz: 60,
                    index: frame,
                    runtime: runtime,
                    tick: frame
                );
            }

            Assert.Equal(expected: 2, actual: source.Produced);
        }
    }

    // A source that hands out an image view alone, as a camera or a capture does, names no image a graph's barriers can
    // name: a graph instance reading it draws a stand-in, and the lease its acquisition returned is retired at once.
    [Fact]
    public void AGraphReadingAViewOnlySourceDrawsAStandIn() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var source = new FakeSource(
            cadence: ImageSourceCadence.Tick,
            gpu: gpu
        ) {
            ViewOnly = true,
        };

        recorders.Registry.RegisterProducer(
            factory: _ => source,
            package: RenderGraphInstance.SourcePackage(producer: ReadProducer)
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
                    name: "pane",
                    reads: new RenderGraphRead(Producer: ReadSource)
                )
            ),
            "pane",
            null!,
            Graph(ScreensGraph(false, "screen"), ("screen", ReadSource))
        );

        for (var index = 0; (index < 3); index++) {
            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: [new RenderGraphFootprint(Consumer: "pane", Height: 1.0, Producer: ReadSource, Width: 1.0)],
                Index: index,
                Roots: [new RenderGraphRoot(Height: 1.0, Instance: "pane", Width: 1.0)],
                Tick: index
            );

            _ = runtime.ProduceFrame(
                context: default,
                frame: in frame
            );
        }

        Assert.True(condition: (source.Acquired > 0));
        Assert.Equal(expected: source.Acquired, actual: source.Released);
    }
    /// <summary>A source producer over one image, declaring a fixed extent at a cadence, every acquisition and release
    /// counted.</summary>
    private sealed class FakeSource(FakePipelineGpu gpu, ImageSourceCadence cadence) : IRenderGraphSourceProducer {
        public const uint Height = 6;
        public const uint Width = 10;

        private IGpuImage? m_image;

        public int Acquired { get; private set; }

        public ImageSourceDescriptor? Descriptor { get; } = new(
            Cadence: cadence,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.External,
            Format: ImagePixelFormat.R8G8B8A8Unorm,
            Height: Height,
            Producer: ReadProducer,
            Transport: ImageSourceTransport.Imported,
            Width: Width
        );

        public (uint Width, uint Height) Extent { get; private set; }
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public nint ImageView => m_image!.ImageViewHandle;
        public string? NotReadyReason => ((Produced == 0) ? "the fake camera has not produced" : null);
        public string? PendingCapturePath => null;
        public int Produced { get; private set; }
        public int Released { get; private set; }
        // Whether the source hands out its image view alone, as one another thread or device writes does.
        public bool ViewOnly { get; init; }

        public IGpuWorkSource Work { get; } = new GpuWorkLedger(
            framesInFlight: 3,
            name: "test.camera"
        );

        public void Dispose() => m_image?.Dispose();
        public void OnDeviceLost() { }
        public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            m_image ??= gpu.Create(
                format: GpuPixelFormat.R8G8B8A8Unorm,
                height: height,
                name: default,
                usage: GpuImageUsage.Sampled,
                width: width
            );
            Extent = (width, height);
            Produced++;

            return true;
        }
        public void RequestCapture(FrameCaptureRequest request) => _ = request.TryFail(error: new NotSupportedException());
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            if (m_image is not { } image) {
                output = default;

                return false;
            }

            Acquired++;
            output = new RenderGraphExternalOutput(
                Image: (ViewOnly ? default : Surface.SameDeviceImage(
                    format: GpuPixelFormat.R8G8B8A8Unorm,
                    height: image.Height,
                    imageHandle: image.ImageHandle,
                    imageViewHandle: image.ImageViewHandle,
                    width: image.Width
                )),
                Layout: GpuImageLayout.ShaderReadOnly,
                Lease: new GpuImageLease(
                    ImageViewHandle: image.ImageViewHandle,
                    Release: _ => Released++
                )
            );

            return true;
        }
    }
    /// <summary>A world producer that records the images it is handed and, when asked, takes their leases as a
    /// submission sampling them would.</summary>
    private sealed class ReadingWorld : IRenderGraphExternalProducer {
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;

        public LeaseRetireList Held { get; } = new();

        public string? NotReadyReason => null;
        public string? PendingCapturePath => null;

        public List<(string Producer, nint ImageView)> Seen { get; } = [];

        public bool Takes { get; set; }

        public IGpuWorkSource Work { get; } = new GpuWorkLedger(
            framesInFlight: 3,
            name: "test.world"
        );

        public void Dispose() { }
        public void OnDeviceLost() { }
        public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            Seen.Clear();

            for (var index = 0; (index < (reads?.Count ?? 0)); index++) {
                Seen.Add(item: (reads![index].Producer, reads[index].Image.ImageViewHandle));

                if (Takes) {
                    Held.Hold(lease: reads.Take(index: index));
                }
            }

            return true;
        }
        public void RequestCapture(FrameCaptureRequest request) => _ = request.TryFail(error: new NotSupportedException());
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            output = default;

            return false;
        }
    }
}
