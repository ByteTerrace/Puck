using System.Buffers;
using System.Numerics;
using Puck.Abstractions.Capture;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
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
    // The frames a conversion settles within once each frame has waited out the build it started: one starts the build,
    // the next installs it and converts, with room for a refused build's retry.
    private const int SettleFrames = 16;

    /// <summary>A desktop capture's CPU route answers a frame from its converted image, never from the pixels it
    /// captured: a captured frame whose conversion refuses answers refused, and never rendered, until a changed build
    /// converts it, and a frame no conversion reads refuses even with an older image still shown.</summary>
    [Fact]
    public void ACapturedFrameAnswersFromItsConversionNeverFromItsPixels() {
        using var scene = new Scene(shown: Pattern(), trackObjects: true);
        using var capture = new WorldCapturePixels(name: "capture:law");
        using var camera = new WorldCameraSourceFeed(cameras: new CapturedSeatCameras(pixels: capture), profile: null, seat: 2, sensor: WorldCameraSensor.Infrared);
        var source = new CpuFrameSource();

        FrameRender Pull() {
            Assert.True(condition: capture.Pull(context: default, runtime: scene.Runtime, source: source));

            var answer = capture.Answer();

            Assert.Equal(expected: answer, actual: camera.Publish(context: default));
            Assert.Equal(expected: answer, actual: WorldCaptureFrame.Answer(
                ended: false,
                fault: null,
                gpuHandle: 0x5A,
                gpuRoute: false,
                pixels: capture
            ));

            return answer;
        }

        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: capture.Answer().Completion);

        scene.Faults.Arm(kind: GpuCreationKind.Pipeline, nth: 1);
        TestLiveness.Within(
            building: () => capture.IsBuilding,
            frames: SettleFrames,
            reason: () => (capture.Answer().Reason ?? "the refused conversion answered rendered"),
            step: () => {
                var answer = Pull();

                Assert.False(condition: answer.IsRendered);

                return (answer.Completion == FrameCompletion.Refused);
            }
        );
        Assert.Contains(expectedSubstring: GpuCreationFaults.RefusalCode, actualString: Pull().Reason);
        Assert.Equal(expected: ((nint)0), actual: capture.Handle);

        scene.Faults.Disarm();
        TestLiveness.Within(
            building: () => capture.IsBuilding,
            frames: SettleFrames,
            reason: () => (capture.Answer().Reason ?? "the rebuilt conversion never rendered"),
            step: () => Pull().IsRendered
        );
        Assert.NotEqual(expected: ((nint)0), actual: capture.Handle);

        source.Shared = true;
        Assert.Equal(expected: FrameCompletion.Refused, actual: Pull().Completion);
        Assert.Contains(expectedSubstring: "no conversion reads", actualString: capture.Answer().Reason);

        var held = capture.Acquire();
        var released = scene.ImagesReleased;

        capture.Forget();
        Assert.Equal(expected: released, actual: scene.ImagesReleased);
        Assert.Equal(expected: ((nint)0), actual: capture.Handle);
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: capture.Answer().Completion);
        source.Shared = false;
        Assert.True(condition: capture.Pull(context: default, runtime: null, source: source));
        Assert.Equal(expected: ((nint)0), actual: capture.Handle);
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: camera.Publish(context: default).Completion);
        Assert.Equal(expected: released, actual: scene.ImagesReleased);
        held.Retire();
        Assert.True(condition: (scene.ImagesReleased > released), userMessage: "the forgotten source's converter outlived its last lease");
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ACapturedFrameSettlesItsConversionWhenNoNewerFrameArrives(bool refused) {
        using var scene = new Scene(shown: Pattern());
        using var capture = new WorldCapturePixels(name: "capture:quiet-law");
        var source = new CpuFrameSource { ReusesBuffer = true };

        if (refused) {
            scene.Faults.Arm(kind: GpuCreationKind.Pipeline, nth: 1);
        }

        scene.HoldPipelines();
        Assert.True(condition: capture.Pull(context: default, runtime: scene.Runtime, source: source));
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: capture.Answer().Completion);
        source.Available = false;
        scene.ReleasePipelines();
        TestLiveness.Within(
            building: () => capture.IsBuilding,
            frames: SettleFrames,
            reason: () => (capture.Answer().Reason ?? "the quiet capture never settled its conversion"),
            step: () => {
                Assert.False(condition: capture.Pull(context: default, runtime: scene.Runtime, source: source));

                return (capture.Answer().Completion == (refused ? FrameCompletion.Refused : FrameCompletion.Rendered));
            }
        );

        if (refused) {
            Assert.Contains(expectedSubstring: GpuCreationFaults.RefusalCode, actualString: capture.Answer().Reason);
            scene.Faults.Disarm();
            TestLiveness.Within(
                building: () => capture.IsBuilding,
                frames: SettleFrames,
                reason: () => (capture.Answer().Reason ?? "the quiet capture never rebuilt its conversion"),
                step: () => {
                    Assert.False(condition: capture.Pull(context: default, runtime: scene.Runtime, source: source));

                    return capture.Answer().IsRendered;
                }
            );
        }
    }
    [Fact]
    public void ACaptureAnswersOnlyTheImageOfItsRouteAndAnEndedSourceRefusesItsLastFrame() {
        using var capture = new WorldCapturePixels(name: "capture:route-law");
        var source = new CpuFrameSource { Shared = true };

        FrameRender Answer(bool gpuRoute, nint gpuHandle, bool ended = false) => WorldCaptureFrame.Answer(
            ended: ended,
            fault: null,
            gpuHandle: gpuHandle,
            gpuRoute: gpuRoute,
            pixels: capture
        );

        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: Answer(gpuRoute: true, gpuHandle: 0).Completion);
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: Answer(gpuRoute: false, gpuHandle: 0x5A).Completion);
        Assert.True(condition: capture.Pull(context: default, runtime: null, source: source));
        Assert.Equal(expected: FrameCompletion.Refused, actual: Answer(gpuRoute: false, gpuHandle: 0x5A).Completion);
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: Answer(gpuRoute: true, gpuHandle: 0).Completion);
        Assert.True(condition: Answer(gpuRoute: true, gpuHandle: 0x5A).IsRendered);
        Assert.Equal(expected: FrameCompletion.Refused, actual: Answer(ended: true, gpuHandle: 0x5A, gpuRoute: true).Completion);
        Assert.Equal(expected: FrameCompletion.Refused, actual: Answer(ended: true, gpuHandle: 0x5A, gpuRoute: false).Completion);
        capture.Forget();
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: Answer(gpuRoute: false, gpuHandle: 0x5A).Completion);

        // An ended source refuses even when its CPU frame is waiting on a conversion that would not refuse.
        source.Shared = false;
        Assert.True(condition: capture.Pull(context: default, runtime: null, source: source));
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: Answer(gpuRoute: false, gpuHandle: 0).Completion);
        Assert.Equal(expected: FrameCompletion.Refused, actual: Answer(ended: true, gpuHandle: 0, gpuRoute: false).Completion);
    }
    [Fact]
    public void AnUnopenedImportedSourceRefusesWithoutAScheduledExtent() {
        using var scene = new Scene(shown: Desktop(), openingFault: "the capture window is gone");

        scene.Compose();
        Assert.Equal(expected: FrameCompletion.Refused, actual: scene.Runtime.Render.Completion);
        Assert.Contains(expectedSubstring: "the capture window is gone", actualString: scene.Runtime.Render.Reason);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ARefusedFillConversionRefusesTheRootAndAChangedBuildCanRenderIt(bool deviceLost) {
        using var scene = new Scene(shown: Desktop(), alwaysFills: true);

        scene.Faults.Arm(kind: GpuCreationKind.Pipeline, nth: 1);
        TestLiveness.Within(
            building: () => scene.Fills.IsBuilding,
            frames: SettleFrames,
            reason: () => (scene.Runtime.Render.Reason ?? "the fill refusal never reached the root"),
            step: () => {
                scene.Compose();

                return (scene.Runtime.Render.Completion == FrameCompletion.Refused);
            }
        );
        Assert.Contains(expectedSubstring: GpuCreationFaults.RefusalCode, actualString: scene.Runtime.Render.Reason);
        Assert.False(condition: FillConverted(fills: scene.Fills));

        scene.Compose();
        Assert.Equal(expected: FrameCompletion.Refused, actual: scene.Runtime.Render.Completion);
        if (deviceLost) {
            var revision = scene.Faults.Revision;

            scene.Fills.OnDeviceLost();
            Assert.Equal(expected: revision, actual: scene.Faults.Revision);
        } else {
            scene.Faults.Disarm();
        }
        TestLiveness.Within(
            building: () => scene.Fills.IsBuilding,
            frames: SettleFrames,
            reason: () => (scene.Runtime.Render.Reason ?? "the replacement fill never rendered"),
            step: () => {
                scene.Compose();

                return scene.Runtime.Render.IsRendered;
            }
        );
        Assert.True(condition: FillConverted(fills: scene.Fills));
    }
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 30)]
    [InlineData(true, 30)]
    [Theory]
    public void ASourceWithNoExtentAnswersItsFeedOrItsOffscreenFill(bool alwaysFills, int displayHertz) {
        using var scene = new Scene(shown: Desktop(), alwaysFills: alwaysFills, feedExtent: 0U);
        var producer = Assert.IsType<WorldImageFeedProducer>(@object: scene.Runtime.Producer(instance: 0));
        var feed = Assert.IsType<FakeFeed>(@object: producer.Feed);

        scene.DisplayHertz = displayHertz;
        feed.Answer = FrameRender.Waiting(reason: "probe starting on another thread");
        scene.HoldPipelines();
        scene.Compose();
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: scene.Runtime.Render.Completion);

        feed.Answer = FrameRender.Refused(reason: "probe never opened its kernel");
        scene.Compose();
        Assert.Equal(expected: (alwaysFills ? FrameCompletion.NotYetRenderable : FrameCompletion.Refused), actual: scene.Runtime.Render.Completion);
        scene.ReleasePipelines();

        if (alwaysFills) {
            TestLiveness.Within(
                building: () => scene.Fills.IsBuilding,
                frames: SettleFrames,
                reason: () => (scene.Runtime.Render.Reason ?? "the fill never rendered without a probe extent"),
                step: () => {
                    scene.Compose();

                    return scene.Runtime.Render.IsRendered;
                }
            );
            Assert.NotEqual(expected: ((nint)0), actual: scene.Read(producer: producer));
        } else {
            feed.Answer = FrameRender.Waiting(reason: "probe restarted");
            scene.Compose();
            Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: scene.Runtime.Render.Completion);
        }
    }

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

        Assert.True(condition: fill.Image.IsSameDeviceImage);
        Assert.Equal(fill.ImageViewHandle, fill.Image.ImageViewHandle);
        Assert.Equal((1U, 1U), (fill.Image.Width, fill.Image.Height));
        Assert.Equal(GpuPixelFormat.R16G16B16A16Float, fill.Image.Format);
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
    private sealed class FakeCaptureProducer(uint extent, string? openingFault) : IWorldImageProducer {
        public ImageContentClass Content => ImageContentClass.External;
        public string Id => WorldImageProducerSettings.CaptureId;
        public ImageSourceTransport Transport => ImageSourceTransport.Imported;

        public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
            if (openingFault is not null) {
                feed = null;
                fault = openingFault;

                return false;
            }

            feed = new FakeFeed(descriptor: new ImageSourceDescriptor(
                Cadence: ImageSourceCadence.Rate(rateHz: 30U),
                Color: ImageColorEncoding.Srgb,
                Content: Content,
                Format: ImagePixelFormat.B8G8R8A8Unorm,
                Height: extent,
                Producer: Id,
                Transport: Transport,
                Width: extent
            ));
            fault = null;

            return true;
        }
    }
    // An imported feed whose image is one fixed handle, counting its acquisitions.
    private sealed class FakeFeed(ImageSourceDescriptor descriptor) : IWorldImportFeed {
        public static readonly nint DesktopHandle = 0xDE5C;

        public int Acquisitions { get; private set; }

        public FrameRender Answer { get; set; } = FrameRender.Rendered;
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
        public FrameRender Publish(in FrameContext context) => Answer;
    }
    // One screen showing a source, run as the binder runs it: the source is a render-graph instance whose producer reads
    // through the capture gate and the fills, and each frame converts the fills it needs before its source resolves, on a
    // device of its own whose every pipeline creation can be held.
    private sealed class Scene : IDisposable {
        private readonly FakeGpuDevice m_gpu;

        private readonly ManualResetEventSlim m_held = new(initialState: true);

        private int m_pipelinesEntered;
        private long m_frame;

        public Scene(WorldScreenSource shown, bool alwaysFills = false, uint feedExtent = 1U, string? openingFault = null, bool trackObjects = false) {
            m_gpu = new FakeGpuDevice(trackObjects: trackObjects);
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

            producers.Register(producer: new FakeCaptureProducer(extent: feedExtent, openingFault: openingFault));
            producers.Register(producer: new WorldTestPatternProducer());
            SourceConversionPackage.RegisterAll(packages: packages);
            producers.RegisterPackages(
                adapt: opening => new WorldImageFeedProducer(
                    fill: Fills.Acquire,
                    fillRender: Fills.RenderOf,
                    gate: Gate,
                    opening: opening
                ),
                packages: packages
            );
            Assert.True(condition: RenderGraphInstanceSet.TryCreate(
                instances: WorldSourceInstances.Of(shown: [shown], world: WorldDefinitionLoader.BootInstanceName).Instances,
                refusal: out var setRefusal,
                set: out var set
            ), userMessage: setRefusal?.Message);
            Assert.True(condition: RenderGraphRuntime.TryCreate(
                deviceContext: new FaultingDevice(gpu: m_gpu, faults: Faults),
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
        public int DisplayHertz { get; set; }

        public GpuCreationFaults Faults { get; } = new();

        // The images the fake device created and released, when the scene tracks objects.
        public int ImagesReleased => m_gpu.Created.Where(predicate: static item => item.Kind.Contains(value: "image")).Sum(selector: static item => item.DisposeCount);
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
        public void Compose() {
            _ = Frame();
            var frame = new RenderGraphFrame(
                Index: m_frame++,
                Tick: 1L,
                DisplayWidth: 8,
                DisplayHeight: 8,
                DisplayHertz: DisplayHertz,
                Footprints: [],
                Roots: [new RenderGraphRoot(Instance: Runtime.Root, Width: 1.0, Height: 1.0)]
            );

            _ = Runtime.ProduceFrame(context: default, frame: in frame);
        }
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
    // A capture source handing over one 2x2 B8G8R8A8 frame: CPU pixels, or a shared texture no conversion reads.
    private sealed class CpuFrameSource : IFrameCaptureSource {
        private readonly byte[] m_pixels = new byte[16];

        private PoisonedPixels? m_handed;

        public bool Available { get; set; } = true;
        // Whether a frame's pixels are valid only until the next capture attempt, as a platform's reused buffer is.
        public bool ReusesBuffer { get; set; }
        public bool Shared { get; set; }

        public bool TryCapture(out Surface surface) {
            m_handed?.Poison();
            m_handed = null;

            if (!Available) {
                surface = default;

                return false;
            }

            if (ReusesBuffer && !Shared) {
                m_handed = new PoisonedPixels(pixels: m_pixels);
                surface = Surface.CpuPixels(format: GpuPixelFormat.B8G8R8A8Unorm, height: 2U, pixels: m_handed.Memory, width: 2U);

                return true;
            }

            surface = (Shared
                ? Surface.SharedTexture(format: GpuPixelFormat.B8G8R8A8Unorm, height: 2U, sharedHandle: 0x5A, width: 2U)
                : Surface.CpuPixels(format: GpuPixelFormat.B8G8R8A8Unorm, height: 2U, pixels: m_pixels, width: 2U));

            return true;
        }
    }
    // One handed-over frame's pixels, which throw when read after the source took them back.
    private sealed class PoisonedPixels(byte[] pixels) : MemoryManager<byte> {
        private bool m_poisoned;

        public void Poison() => m_poisoned = true;
        public override Span<byte> GetSpan() => (m_poisoned
            ? throw new InvalidOperationException(message: "the capture source already reused this frame's pixels")
            : pixels);
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
    // A seat whose CPU sensor answers its real conversion. Handle deliberately remains the previously converted
    // image, even when a later conversion refuses, so Publish cannot infer availability from the handle.
    private sealed class CapturedSeatCameras(WorldCapturePixels pixels) : IWorldSeatCameras {
        public GpuImageLease Acquire(int seat, WorldCameraSensor sensor) => pixels.Acquire();
        public FrameRender Answer(int seat, WorldCameraSensor sensor) => pixels.Answer();
        public (uint Width, uint Height)? Extent(int seat, WorldCameraSensor sensor) => (2U, 2U);
        public string? Fault(int seat, WorldCameraSensor sensor) => pixels.Answer().Reason;
        public nint Handle(int seat, WorldCameraSensor sensor) => pixels.Handle;
        public Vector3 Light(int seat, WorldCameraSensor sensor) => pixels.Light;
    }
    private sealed class FaultingDevice(IGpuDeviceContext gpu, GpuCreationFaults faults) : IGpuDeviceContext {
        public long AdapterLuid => gpu.AdapterLuid;
        public GpuDeviceCapabilities? Capabilities => gpu.Capabilities;
        public GpuDeviceIdentity? Identity => gpu.Identity;
        public GpuMemoryProfile MemoryProfile => gpu.MemoryProfile;
        public GpuDeviceServices Services { get; } = GpuCreationFaults.Wrap(faults: faults, services: gpu.Services);

        public void WaitIdle() => gpu.WaitIdle();
    }
}
