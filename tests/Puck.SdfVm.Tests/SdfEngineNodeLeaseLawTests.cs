using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfEngineNode"/> as the external producer behind <c>sdf.world</c>, over
/// <see cref="FakeGpuDevice"/>: it hands out its engine's latest completed output as a lease on every frame, produced or
/// not; it counts every acquisition; a view output its engine replaces at a new extent, and an engine it replaces at a
/// larger one, are disposed only once every acquisition of them is released, the view output also only once the frame
/// ring has passed every submission that wrote it.
/// </summary>
public sealed class SdfEngineNodeLeaseLawTests {
    private const uint Extent = 64;

    [Fact]
    public void TheLatestOutputIsHandedOutUntilTheNextFrameProducesOne() {
        using var rig = new Rig();

        Assert.False(condition: rig.Node.TryAcquireOutput(output: out _));

        rig.ProduceFirst();
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var first));
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var second));

        // A frame nothing produced hands out the same image again, each acquisition counted.
        Assert.Equal(
            actual: (second.Image.ImageViewHandle, second.Image.Width, second.Image.Height, second.Image.Format, second.Layout, rig.Node.OutputLeases),
            expected: (first.Image.ImageViewHandle, Extent, Extent, GpuPixelFormat.R8G8B8A8Unorm, GpuImageLayout.ShaderReadOnly, 2)
        );
        Assert.Equal(
            actual: first.Lease.ImageViewHandle,
            expected: first.Image.ImageViewHandle
        );

        first.Lease.Retire();
        second.Lease.Retire();
        Assert.Equal(
            actual: rig.Node.OutputLeases,
            expected: 0
        );
    }
    // A consumer's planned barriers start from the lease's declared layout and hand the image back in it, so it must be
    // the layout the engine's own recording leaves the output in, which is also the one its next frame starts from.
    [Fact]
    public void ALeaseDeclaresTheLayoutTheEngineLeavesItsOutputIn() {
        using var rig = new Rig();

        rig.Gpu.ImageTransitions = [];
        rig.ProduceFirst();
        Assert.True(condition: rig.Node.Produce(
            context: rig.Context,
            height: Extent,
            width: Extent
        ));
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var output));

        var transitions = rig.Gpu.ImageTransitions.Where(predicate: transition => (transition.Image == output.Image.ImageHandle)).ToArray();

        Assert.Equal(
            actual: transitions[^1].New,
            expected: output.Layout
        );
        Assert.Contains(
            collection: transitions,
            filter: transition => (transition.Old == output.Layout)
        );
        output.Lease.Retire();
    }
    [Fact]
    public void AViewOutputReplacedWhileLeasedIsHeldUntilReleaseAndTheEngineIsKept() {
        using var rig = new Rig();

        rig.ProduceFirst();
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var held));

        // A smaller extent resizes the view's output inside the engine; the replaced output is held while leased.
        Assert.True(condition: rig.Node.Produce(
            context: rig.Context,
            height: (Extent / 2),
            width: (Extent / 2)
        ));
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var current));
        Assert.Equal(
            actual: (rig.Node.RetiringEngines, rig.Node.OutputLeases, current.Image.Width, current.Image.Height),
            expected: (0, 2, (Extent / 2), (Extent / 2))
        );
        held.Lease.Retire();
        Assert.Equal(
            actual: rig.Node.OutputLeases,
            expected: 1
        );

        current.Lease.Retire();
        Assert.Equal(
            actual: rig.Node.OutputLeases,
            expected: 0
        );
    }
    /// <summary>A view output replaced at a new extent is released by the first frame after both its acquisitions are
    /// released and the ring has retired every submission that wrote it: never while a consumer holds it, however many
    /// frames pass, and once released, at the next frame.</summary>
    [Fact]
    public void AReplacedViewOutputIsReleasedOnlyOnceItsHoldsReachZeroAndTheRingHasPassedIt() {
        using var rig = new Rig(trackObjects: true);

        rig.ProduceFirst();
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var held));

        var before = rig.LiveImages();

        rig.Produce(extent: (Extent / 2));

        for (var frame = 0; (frame <= SdfWorldEngine.FrameRingSize); frame++) {
            rig.Produce(extent: (Extent / 2));
        }

        Assert.All(
            action: static creation => Assert.Equal(actual: creation.DisposeCount, expected: 0),
            collection: before
        );

        held.Lease.Retire();
        rig.Produce(extent: (Extent / 2));
        Assert.Equal(
            actual: Assert.Single(collection: before, predicate: static creation => (creation.DisposeCount != 0)).DisposeCount,
            expected: 1
        );
    }
    /// <summary>A replaced view output nothing holds is still written by the frame before its replacement, whose ring
    /// slot's fence the engine waits only when the ring comes round to it, so the output outlives the replacing frame
    /// and the one after, and the frame after those releases it.</summary>
    [Fact]
    public void AnUnheldReplacedViewOutputOutlivesTheFramesThatMayStillBeWritingIt() {
        using var rig = new Rig(trackObjects: true);

        rig.ProduceFirst();

        var before = rig.LiveImages();

        for (var frame = 0; (frame < SdfWorldEngine.FrameRingSize); frame++) {
            rig.Produce(extent: (Extent / 2));
            Assert.All(
                action: static creation => Assert.Equal(actual: creation.DisposeCount, expected: 0),
                collection: before
            );
        }

        rig.Produce(extent: (Extent / 2));
        Assert.Equal(
            actual: Assert.Single(collection: before, predicate: static creation => (creation.DisposeCount != 0)).DisposeCount,
            expected: 1
        );
    }
    [Fact]
    public void AnEngineReplacedAtALargerExtentWhileLeasedIsDisposedOnlyAfterRelease() {
        using var rig = new Rig();

        rig.ProduceFirst();
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var held));

        // An extent past the engine's replaces the engine; the old one is held while its output is leased.
        Assert.True(condition: rig.Node.Produce(
            context: rig.Context,
            height: (Extent * 2),
            width: (Extent * 2)
        ));
        Assert.Equal(
            actual: (rig.Node.RetiringEngines, rig.Node.OutputLeases, rig.Node.IsReady),
            expected: (1, 1, true)
        );
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var current));
        Assert.Equal(
            actual: (current.Image.Width, current.Image.Height),
            expected: ((Extent * 2), (Extent * 2))
        );
        held.Lease.Retire();
        Assert.Equal(
            actual: (rig.Node.RetiringEngines, rig.Node.OutputLeases),
            expected: (0, 1)
        );

        current.Lease.Retire();
        Assert.Equal(
            actual: rig.Node.OutputLeases,
            expected: 0
        );
    }
    [Fact]
    public void AnEngineReplacedWithNothingLeasedIsDisposedAtOnce() {
        using var rig = new Rig();

        rig.ProduceFirst();
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var released));
        released.Lease.Retire();

        Assert.True(condition: rig.Node.Produce(
            context: rig.Context,
            height: (Extent * 2),
            width: Extent
        ));
        Assert.Equal(
            actual: (rig.Node.RetiringEngines, rig.Node.OutputLeases),
            expected: (0, 0)
        );
    }
    [Fact]
    public void ADeviceLossReleasesEveryHeldEngine() {
        using var rig = new Rig();

        rig.ProduceFirst();
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var old));
        Assert.True(condition: rig.Node.Produce(
            context: rig.Context,
            height: (Extent / 2),
            width: (Extent / 2)
        ));
        Assert.True(condition: rig.Node.TryAcquireOutput(output: out var current));

        rig.Node.OnDeviceLost();
        Assert.Equal(
            actual: (rig.Node.RetiringEngines, rig.Node.OutputLeases),
            expected: (0, 0)
        );
        Assert.False(condition: rig.Node.TryAcquireOutput(output: out _));

        // The consumer's list retires its leases after the loss too; neither touches the rebuilt engine's count.
        old.Lease.Retire();
        current.Lease.Retire();
        rig.ProduceFirst();
        Assert.Equal(
            actual: rig.Node.OutputLeases,
            expected: 0
        );
    }
    [Fact]
    public void ASteadyFrameAcquiringAndReleasingAllocatesNothing() {
        using var rig = new Rig();

        void Frame() {
            _ = rig.Node.Produce(
                context: rig.Context,
                height: Extent,
                width: Extent
            );

            if (rig.Node.TryAcquireOutput(output: out var output)) {
                output.Lease.Retire();
            }
        }

        rig.ProduceFirst();

        for (var warm = 0; (warm < 4); warm++) {
            Frame();
        }

        Assert.Equal(
            actual: AllocationWindow.Least(window: Frame),
            expected: 0L
        );
    }

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public int Captures { get; private set; }
        public SdfFrame Frame { get; set; } = frame;

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            Captures++;

            return Frame;
        }
    }

    // An image another device writes is sampled only after that device's fence reaches the value its write signals:
    // the wait rides the screen's lease into the frame submission that samples it, never into an earlier submission,
    // and a lease retires only once that submission's frame-ring slot fence has passed.
    [Fact]
    public void AScreensExternalWaitLandsInTheSubmissionThatSamplesIt() {
        using var fence = new SharedFence();
        var released = 0;

        const string Source = "source$camera$0";
        var reads = new RenderGraphExternalReads(producers: [Source]);

        using var rig = new Rig(screenSources: new ScreenSources(
            readOf: static _ => Source,
            screens: [0]
        ));

        void Produce() {
            reads.Bind(
                image: default,
                index: 0,
                layout: GpuImageLayout.ShaderReadOnly,
                lease: new GpuImageLease(
                    ImageViewHandle: 0x51,
                    Release: _ => released++,
                    Wait: new GpuExternalWait(
                        Fence: fence,
                        Value: 7UL
                    )
                ),
                tainted: false
            );
            Assert.True(condition: rig.Node.Produce(
                context: rig.Context,
                height: Extent,
                reads: reads,
                width: Extent
            ));
            reads.RetireUntaken();
        }

        rig.ProduceFirst();
        Produce();

        var (submission, fenced, wait) = Assert.Single(collection: rig.Gpu.CarriedWaits);

        Assert.Equal(
            actual: (submission, fenced, wait.Value, wait.Fence),
            expected: (rig.Gpu.Submissions, true, 7UL, ((IGpuSharedFence)fence))
        );
        Assert.Equal(actual: released, expected: 0);

        // Each later frame's lease carries its own wait into its own submission; a slot's lease retires only once the
        // frame ring comes back to that slot.
        for (var frame = 1; (frame <= SdfWorldEngine.FrameRingSize); frame++) {
            Produce();
            Assert.Equal(expected: (frame + 1), actual: rig.Gpu.CarriedWaits.Count);
            Assert.Equal(expected: (rig.Gpu.Submissions, true), actual: (rig.Gpu.CarriedWaits[^1].Submission, rig.Gpu.CarriedWaits[^1].Fenced));
        }

        Assert.Equal(actual: released, expected: 1);
    }
    // A screen reading a source instance binds the image the graph hands the frame, and the node takes that read's lease
    // once however many screens show it: the runtime's retirement of untaken leases leaves it held, and it retires only
    // once the frame ring comes back to the slot whose submission sampled it. A frame produced outside the graph hands the
    // screen nothing, and it binds nothing.
    [Fact]
    public void AScreensSourceLeaseRetiresOnlyAfterTheSamplingSlotsFence() {
        const string Source = "source$camera$0";
        var released = new List<int>();

        using var rig = new Rig(screenSources: new ScreenSources(
            readOf: static _ => Source,
            screens: [0, 3]
        ));

        rig.ProduceFirst();
        Assert.Equal(
            actual: (rig.Node.BoundScreenSource(screen: 0), rig.Node.BoundScreenSource(screen: 3)),
            expected: (((nint)0), ((nint)0))
        );

        var reads = new RenderGraphExternalReads(producers: [Source]);

        for (var frame = 0; (frame <= SdfWorldEngine.FrameRingSize); frame++) {
            reads.Bind(
                image: default,
                index: 0,
                layout: GpuImageLayout.ShaderReadOnly,
                lease: new GpuImageLease(
                    ImageViewHandle: (0x60 + frame),
                    Release: released.Add,
                    ReleaseToken: frame
                ),
                tainted: false
            );
            Assert.True(condition: rig.Node.Produce(
                context: rig.Context,
                height: Extent,
                reads: reads,
                width: Extent
            ));
            Assert.True(condition: reads.IsTaken(index: 0));
            reads.RetireUntaken();
            Assert.Equal(
                actual: (rig.Node.BoundScreenSource(screen: 0), rig.Node.BoundScreenSource(screen: 3)),
                expected: (((nint)(0x60 + frame)), ((nint)(0x60 + frame)))
            );

            if (frame < SdfWorldEngine.FrameRingSize) {
                Assert.Empty(collection: released);
            }
        }

        Assert.Equal(
            actual: released,
            expected: new[] { 0 }
        );
    }
    // A view filming the world takes the frame the node renders next before the node renders it: the frame source captures
    // once, the node's render takes that frame, and its next render captures anew.
    [Fact]
    public void AViewTakesTheFrameTheNodeRendersNext() {
        using var rig = new Rig();

        rig.ProduceFirst();

        var captures = rig.Frames.Captures;
        var first = rig.Node.HostFrame(context: rig.Context);

        Assert.Same(
            actual: rig.Node.HostFrame(context: rig.Context),
            expected: first
        );
        rig.Produce(extent: Extent);
        Assert.Equal(
            actual: rig.Frames.Captures,
            expected: (captures + 1)
        );
        rig.Produce(extent: Extent);
        Assert.Equal(
            actual: rig.Frames.Captures,
            expected: (captures + 2)
        );
    }
    // A mirror: a screen sampling the node's own view binds the output the view completed before, and the engine renders
    // the frame into another image, reusing a replaced one once nothing holds it, so a steady mirror cycles through at
    // most three images: the one it writes, the one it samples, and the one a ring slot's fence still holds.
    [Fact]
    public void AViewSamplingItsOwnOutputNeverWritesTheImageItSamples() {
        const string Self = "mirror";
        var reads = new RenderGraphExternalReads(producers: [Self]);
        var written = new HashSet<nint>();

        using var rig = new Rig(
            screenSources: new ScreenSources(
                readOf: static _ => Self,
                screens: [0]
            ),
            trackObjects: true
        );

        rig.Gpu.DistinctImages = true;
        rig.ProduceFirst();

        var images = rig.LiveImages().Length;

        for (var frame = 0; (frame < 8); frame++) {
            Assert.True(condition: rig.Node.TryAcquireOutput(output: out var previous));
            reads.Bind(
                image: previous.Image,
                index: 0,
                layout: previous.Layout,
                lease: previous.Lease,
                tainted: previous.Tainted
            );
            Assert.True(condition: rig.Node.Produce(
                context: rig.Context,
                height: Extent,
                reads: reads,
                width: Extent
            ));
            reads.RetireUntaken();
            Assert.True(condition: rig.Node.TryAcquireOutput(output: out var current));
            Assert.Equal(
                actual: rig.Node.BoundScreenSource(screen: 0),
                expected: previous.Image.ImageViewHandle
            );
            Assert.NotEqual(
                actual: current.Image.ImageViewHandle,
                expected: previous.Image.ImageViewHandle
            );
            current.Lease.Retire();
            _ = written.Add(item: current.Image.ImageViewHandle);
        }

        Assert.Equal(actual: written.Count, expected: 3);
        Assert.Equal(actual: rig.LiveImages().Length, expected: (images + 2));
    }
    // The node's output carries the taint of the reads its latest submitted frame bound: a frame that read unfilled
    // external content hands out a tainted output, and the next frame over a filled read an untainted one.
    [Fact]
    public void AnOutputRenderedFromATaintedReadIsHandedOutTaintedUntilAFrameReadsItFilled() {
        const string Source = "source$camera$0";
        var reads = new RenderGraphExternalReads(producers: [Source]);

        using var rig = new Rig(screenSources: new ScreenSources(
            readOf: static screen => Source,
            screens: [0]
        ));

        rig.ProduceFirst();

        bool ProduceOver(bool tainted) {
            reads.Bind(
                image: default,
                index: 0,
                layout: GpuImageLayout.ShaderReadOnly,
                lease: ((nint)0x61),
                tainted: tainted
            );
            Assert.True(condition: rig.Node.Produce(
                context: rig.Context,
                height: Extent,
                reads: reads,
                width: Extent
            ));
            reads.RetireUntaken();
            Assert.True(condition: rig.Node.TryAcquireOutput(output: out var output));
            output.Lease.Retire();

            return output.Tainted;
        }

        Assert.Equal(
            actual: (ProduceOver(tainted: true), ProduceOver(tainted: false)),
            expected: (true, false)
        );
    }
    // Each view output keeps the taint of the frame that last rendered it: a second view rendered over a tainted read
    // stays tainted through a frame that renders the first view alone over a filled one.
    [Fact]
    public void AViewOutputKeepsItsTaintUntilAFrameRendersThatViewAgain() {
        const string Source = "source$camera$0";
        var reads = new RenderGraphExternalReads(producers: [Source]);

        using var rig = new Rig(screenSources: new ScreenSources(
            readOf: static screen => Source,
            screens: [0]
        ));

        var single = rig.Frames.Frame;
        var view = single.Views[0];

        rig.Frames.Frame = (single with {
            Views = [
                (view with {
                    Region = new NormalizedRect(Height: 1f, Width: 0.5f, X: 0f, Y: 0f),
                }),
                (view with {
                    Region = new NormalizedRect(Height: 1f, Width: 0.5f, X: 0.5f, Y: 0f),
                }),
            ],
        });
        rig.ProduceFirst();

        (bool First, bool Second) ProduceOver(bool tainted) {
            reads.Bind(
                image: default,
                index: 0,
                layout: GpuImageLayout.ShaderReadOnly,
                lease: ((nint)0x61),
                tainted: tainted
            );
            Assert.True(condition: rig.Node.Produce(
                context: rig.Context,
                height: Extent,
                reads: reads,
                width: Extent
            ));
            reads.RetireUntaken();
            Assert.True(condition: rig.Node.TryAcquireOutput(output: out var first));
            Assert.True(condition: rig.Node.ViewProducer(view: 1).TryAcquireOutput(output: out var second));
            first.Lease.Retire();
            second.Lease.Retire();

            return (first.Tainted, second.Tainted);
        }

        Assert.Equal(
            actual: ProduceOver(tainted: true),
            expected: (true, true)
        );

        rig.Frames.Frame = single;

        Assert.Equal(
            actual: ProduceOver(tainted: false),
            expected: (false, true)
        );
    }

    private sealed class ScreenSources(IReadOnlyList<int> screens, Func<int, string?> readOf) : ISdfScreenSources {
        public IReadOnlyList<int> Screens => screens;

        public Vector3 Light(int screen) => Vector3.Zero;
        public string? ReadOf(int screen) => readOf(arg: screen);
    }
    private sealed class SharedFence : IGpuSharedFence {
        public ulong CompletedValue => 0UL;

        public void Dispose() { }
    }
    private sealed class Rig : IDisposable {
        public Rig(bool trackObjects = false, ISdfScreenSources? screenSources = null) {
            var gpu = new FakeGpuDevice(
                reportVersion: SdfIsa.Version,
                trackObjects: trackObjects
            );

            Gpu = gpu;
            var builder = new SdfProgramBuilder();

            builder.Sphere(
                material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                radius: 1f
            );

            var frame = new SdfFrame(
                Program: builder.Build(),
                ProgramChanged: false,
                Time: 0f,
                Views: [new SdfViewSnapshot(
                    Camera: CameraSnapshot.LookAt(
                        fieldOfViewRadians: 1f,
                        position: new Vector3(x: 0f, y: 0f, z: -5f),
                        target: Vector3.Zero,
                        viewportHeight: Extent,
                        viewportWidth: Extent
                    ),
                    Region: new NormalizedRect(
                        Height: 1f,
                        Width: 1f,
                        X: 0f,
                        Y: 0f
                    )
                )]
            );

            Frames = new FixedFrameSource(frame: frame);
            Node = new SdfEngineNode(
                brickPoolVoxelCapacity: 0,
                frameSource: Frames,
                height: Extent,
                kernels: SdfTestPipelines.Kernels(),
                pipelines: SdfTestPipelines.Cache(),
                screenSources: screenSources,
                width: Extent
            );
            Context = new FrameContext(
                AccumulatorTicks: 0UL,
                DeltaTicks: 0UL,
                ElapsedTicks: 0UL,
                FrameDeltaTicks: 0UL,
                Host: new HostContext(capabilities: new Dictionary<Type, object> {
                    [typeof(IGpuDeviceContext)] = gpu,
                }),
                StepTicks: 0UL,
                TargetHeight: Extent,
                TargetWidth: Extent
            );
        }

        public FrameContext Context { get; }
        public FixedFrameSource Frames { get; }
        public FakeGpuDevice Gpu { get; }
        public SdfEngineNode Node { get; }

        public void Dispose() => Node.Dispose();
        // The images the device holds now, which a law watches for the one a later frame releases.
        public FakeGpuDevice.Creation[] LiveImages() => [.. Gpu.Created.Where(predicate: static creation => (
            (creation.Kind == "image") &&
            (creation.DisposeCount == 0)
        ))];
        public void Produce(uint extent) => Assert.True(condition: Node.Produce(
            context: Context,
            height: extent,
            width: extent
        ));
        public void ProduceFirst() => _ = Node.ProduceFirstFrame(context: Context);
    }
}
