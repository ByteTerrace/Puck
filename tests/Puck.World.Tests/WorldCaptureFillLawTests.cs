using System.Numerics;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the fill a capture shows in place of external content (<see cref="WorldCaptureFills"/>). A fill converts
/// through a converter whose graph builds off the frame thread, so a fill first converted on the frame a capture is armed
/// for has no image on that frame. While a screen shows an external source (a desktop capture, imported), its fill
/// therefore converts before any capture is armed, and the arming frame's read of the source hands out the converted
/// fill with no pipeline built on that frame. A fill that starts converting on the arming frame, because a screen starts
/// showing external content on it, has no image on that frame, only on a later one: the ordering the first law observes
/// is what gives the arming frame its fill. While nothing shows external content no fill converts, whether a capture is
/// armed on a windowed gate or an offscreen gate fills every frame, so a capture of such a world builds no pipeline.
/// </summary>
public sealed class WorldCaptureFillLawTests {
    private static WorldScreenSource.Producer Desktop() => WorldImageProducerSettings.SourceOf(
        id: WorldImageProducerSettings.CaptureId,
        settings: new WorldCaptureSettings(
            Profile: WorldFeedProfile.Default,
            WindowTitle: "Desktop"
        )
    );
    // Whether the default fill has converted: its image, acquired and released at once.
    private static bool FillConverted(WorldCaptureFills fills) {
        var lease = fills.Acquire(rgba: ImageSourceDescriptor.DefaultCaptureFill);
        var converted = (lease.ImageViewHandle != 0);

        lease.Retire();

        return converted;
    }
    private static WorldScreenSource.Producer Pattern() => WorldImageProducerSettings.SourceOf(
        id: WorldImageProducerSettings.TestPatternId,
        settings: new WorldTestPatternSettings(
            Height: 6,
            Width: 8
        )
    );

    [Fact]
    public void AScreenShowingAnExternalSourceHasItsFillConvertedBeforeTheCaptureIsArmed() {
        var source = Desktop();

        Assert.True(condition: WorldCaptureFills.IsExternal(source: source));

        using var scene = new Scene(shown: source);
        var producer = Assert.IsType<WorldImageFeedProducer>(@object: scene.Runtime.Producer(instance: 0));
        var feed = Assert.IsType<FakeFeed>(@object: producer.Feed);
        var frames = 0;

        // No capture is armed, so the screen shows the desktop's own image while its fill converts.
        TestLiveness.Until(
            reason: () => "The fill never converted while the screen showed an external source and no capture was armed.",
            step: () => {
                Assert.True(condition: scene.Frame());
                Assert.Equal(
                    actual: scene.Read(producer: producer),
                    expected: FakeFeed.DesktopHandle
                );
                frames++;

                return FillConverted(fills: scene.Fills);
            }
        );
        Assert.Equal(expected: frames, actual: feed.Acquisitions);

        var fill = scene.Fills.Acquire(rgba: ImageSourceDescriptor.DefaultCaptureFill);

        fill.Retire();

        // The arming frame's read is the converted fill, never the desktop, and nothing is built on that frame: every
        // pipeline creation blocks, so one reached on the arming frame would leave its read without an image.
        scene.HoldPipelines();
        scene.Armed = true;
        Assert.True(condition: scene.Frame());
        Assert.Equal(
            actual: scene.Read(producer: producer),
            expected: fill.ImageViewHandle
        );
        Assert.NotEqual(expected: FakeFeed.DesktopHandle, actual: fill.ImageViewHandle);
        Assert.Equal(expected: frames, actual: feed.Acquisitions);
        Assert.Equal(expected: 0, actual: scene.PipelinesEntered);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ACaptureWhileNothingShowsExternalContentConvertsNoFillAndBuildsNoPipeline(bool alwaysFills) {
        var source = Pattern();

        Assert.False(condition: WorldCaptureFills.IsExternal(source: source));

        using var scene = new Scene(
            alwaysFills: alwaysFills,
            shown: source
        );

        for (var frame = 0; (frame < 8); frame++) {
            Assert.False(condition: scene.Frame());
        }

        // The gate fills now, but no read resolves through it to a fill, so a capture frame converts nothing either.
        scene.Armed = true;
        Assert.True(condition: scene.Gate.Filling);

        for (var frame = 0; (frame < 8); frame++) {
            Assert.False(condition: scene.Frame());
        }

        Assert.False(condition: FillConverted(fills: scene.Fills));
        Assert.Equal(expected: 0, actual: scene.PipelinesEntered);
    }
    [Fact]
    public void AFillFirstConvertedOnTheArmingFrameHasNoImageUntilALaterFrame() {
        using var scene = new Scene(shown: Pattern());

        // Nothing shows external content, so no fill converts and no pipeline is built.
        for (var frame = 0; (frame < 8); frame++) {
            Assert.False(condition: scene.Frame());
        }

        Assert.False(condition: FillConverted(fills: scene.Fills));
        Assert.Equal(expected: 0, actual: scene.PipelinesEntered);

        // A screen starts showing external content on the arming frame, which starts the fill's conversion, whose build is
        // held in the driver: that frame has no fill.
        scene.HoldPipelines();
        scene.ConsumesExternal = true;
        scene.Armed = true;
        Assert.True(condition: scene.Frame());
        Assert.True(
            condition: scene.PipelineEntered.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken),
            userMessage: "The arming frame started no fill conversion."
        );
        Assert.False(condition: FillConverted(fills: scene.Fills));

        // Once the build finishes, a later frame converts it.
        scene.ReleasePipelines();
        TestLiveness.Until(
            reason: () => "The fill never converted after its build was released.",
            step: () => {
                Assert.True(condition: scene.Frame());

                return FillConverted(fills: scene.Fills);
            }
        );
    }

    // A desktop-capture producer standing in for the real one: every feed it opens is a fake whose image is one fixed
    // handle.
    private sealed class FakeCaptureProducer : IWorldImageProducer {
        public ImageContentClass Content => ImageContentClass.External;
        public string Id => WorldImageProducerSettings.CaptureId;
        public ImageSourceTransport Transport => ImageSourceTransport.Imported;

        public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
            feed = new FakeFeed(descriptor: new ImageSourceDescriptor(
                Cadence: ImageSourceCadence.Rate(rateHz: 30U),
                Color: ImageColorEncoding.Srgb,
                Content: Content,
                Format: ImagePixelFormat.B8G8R8A8Unorm,
                Height: 1U,
                Producer: Id,
                Transport: Transport,
                Width: 1U
            ));
            fault = null;

            return true;
        }
    }
    // An imported feed whose image is one fixed handle, counting its acquisitions.
    private sealed class FakeFeed(ImageSourceDescriptor descriptor) : IWorldImportFeed {
        public static readonly nint DesktopHandle = 0xDE5C;

        public int Acquisitions { get; private set; }
        public ImageSourceDescriptor Descriptor { get; } = descriptor;
        public string? Fault => null;
        public Vector3 Light => Vector3.One;

        public GpuImageLease AcquireFrame() {
            Acquisitions++;

            return DesktopHandle;
        }
        public void Dispose() { }
        public nint Handle() => DesktopHandle;
        public void NotifyDeviceLost() { }
        public void Publish(in FrameContext context) { }
    }
    // One screen showing a source, run as the binder runs it: the source is a render-graph instance whose producer reads
    // through the capture gate and the fills, and each frame converts the fills it needs before its source resolves, on a
    // device of its own whose every pipeline creation can be held.
    private sealed class Scene : IDisposable {
        private readonly FakeGpuDevice m_gpu = new();
        private readonly ManualResetEventSlim m_held = new(initialState: true);

        private int m_pipelinesEntered;

        public Scene(WorldScreenSource shown, bool alwaysFills = false) {
            ConsumesExternal = WorldCaptureFills.IsExternal(source: shown);
            Gate = new WorldCaptureGate(
                alwaysFills: alwaysFills,
                captureArmed: () => Armed
            );
            Fills = new WorldCaptureFills(consumesExternal: () => ConsumesExternal);
            m_gpu.BeforeComputePipeline = description => {
                _ = Interlocked.Increment(location: ref m_pipelinesEntered);
                PipelineEntered.Set();
                m_held.Wait();
            };

            var pipelines = new GpuPassPipelineCache();
            var producers = new WorldImageProducers();
            // The fake device reports no memory, so a fill's region stages and its graph builds the region copy too.
            var packages = new RenderGraphPackageRecorders(regionCopy: new GpuRegionCopyPass(
                kernel: new byte[] { 1 },
                pipelines: pipelines
            ));

            producers.Register(producer: new FakeCaptureProducer());
            producers.Register(producer: new WorldTestPatternProducer());
            SourceConversionPackage.RegisterAll(packages: packages);
            producers.RegisterPackages(
                adapt: opening => new WorldImageFeedProducer(
                    fill: Fills.Acquire,
                    gate: Gate,
                    opening: opening
                ),
                packages: packages
            );
            Assert.True(condition: RenderGraphInstanceSet.TryCreate(
                instances: WorldSourceInstances.Of(shown: [shown]).Instances,
                refusal: out var setRefusal,
                set: out var set
            ), userMessage: setRefusal?.Message);
            Assert.True(condition: RenderGraphRuntime.TryCreate(
                deviceContext: m_gpu,
                graphs: new RenderGraphRuntimeGraph?[set.Instances.Count],
                hostsOnDirectX: false,
                packages: packages,
                pipelines: pipelines,
                refusal: out var refusal,
                root: set.Instances[0].Name,
                runtime: out var runtime,
                set: set
            ), userMessage: refusal?.Message);
            Runtime = runtime;
        }

        public bool Armed { get; set; }
        // Whether a consumer shows external content: at first, whether the scene's screen does.
        public bool ConsumesExternal { get; set; }
        public WorldCaptureFills Fills { get; }
        public WorldCaptureGate Gate { get; }

        public ManualResetEventSlim PipelineEntered { get; } = new(initialState: false);

        public int PipelinesEntered => Volatile.Read(location: ref m_pipelinesEntered);
        public RenderGraphRuntime Runtime { get; }

        public void Dispose() {
            // A build held in the driver finishes before its last release waits for it.
            ReleasePipelines();
            Fills.Dispose();
            Runtime.Dispose();
            m_held.Dispose();
            PipelineEntered.Dispose();
        }
        // One produced frame's publish, as the binder's: the fills the frame needs convert.
        public bool Frame() => Fills.Begin(
            context: default,
            runtime: Runtime
        );
        // Holds every pipeline creation from here on until ReleasePipelines, counting from zero.
        public void HoldPipelines() {
            m_held.Reset();
            PipelineEntered.Reset();
            _ = Interlocked.Exchange(
                location1: ref m_pipelinesEntered,
                value: 0
            );
        }
        // The image the source's producer hands a frame that samples it, released at once.
        public nint Read(WorldImageFeedProducer producer) {
            if (!producer.TryAcquireOutput(output: out var output)) {
                return 0;
            }

            output.Lease.Retire();

            return output.Lease.ImageViewHandle;
        }
        public void ReleasePipelines() => m_held.Set();
    }
}
