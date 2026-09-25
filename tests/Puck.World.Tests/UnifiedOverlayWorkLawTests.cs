using System.Runtime.CompilerServices;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Overlays;
using Puck.Abstractions.Counting;
using Puck.Testing;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the GPU work <see cref="UnifiedOverlayNode"/> counts, driven over <see cref="FakeGpuDevice"/>: the exact
/// counts of a drawn overlay frame, submission identity across a device loss, that the descriptor pool it states is the
/// one it creates, that a pool the device's heap cannot admit is refused by name before anything is created while the
/// inner frame passes through, and created once another owner returns heap space, that a steady-state drawn frame
/// allocates nothing, and that every creation of its
/// resources failed in turn through <see cref="GpuCreationFaults"/> releases exactly what was created before it,
/// presents the inner frame unchanged without throwing, tries nothing again until a device loss, and creates the
/// resources after one.
/// </summary>
public sealed class UnifiedOverlayWorkLawTests {
    // The second drawn cursor frame, published when the third frame polls its fence. The overlay pass is the render
    // pass and its draw: one pipeline bind, one descriptor-set bind, the 48-byte push block. Outside it: the command
    // buffer, the eight frame-slot sampler rewrites (the world image's own binding is rewritten only when its view
    // changes), and the upload of the frame's packed cursor records.
    private const string SecondDrawnFrame =
        "work submission=2 revision=1\nwork overlay executed: dispatches=0 dispatches.indirect=0 draws=1 render-passes=1 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=1 push-constants=48 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=8 uploads.host-visible=112 clears=0\n";

    [Fact]
    public void ADrawnFrameCountsTheOverlayPassExactly() {
        using var rig = new Rig();

        rig.Produce();
        rig.Produce();
        rig.Produce();

        Assert.Equal(
            expected: SecondDrawnFrame,
            actual: rig.Report()
        );
    }
    [Fact]
    public void SubmissionIdentityKeepsIncreasingAcrossADeviceLoss() {
        using var rig = new Rig();
        var sample = new GpuWorkSample();

        rig.Produce();
        rig.Produce();
        Assert.True(condition: rig.Node.Work.TryReadCompleted(sample: sample));

        var before = sample.Submission;

        rig.Node.OnDeviceLost();
        Assert.False(condition: rig.Node.Work.TryReadCompleted(sample: sample));

        rig.Produce();
        rig.Produce();
        Assert.True(condition: rig.Node.Work.TryReadCompleted(sample: sample));
        Assert.True(
            condition: (sample.Submission > before),
            userMessage: $"The rebuilt overlay's submission {sample.Submission} did not follow {before}."
        );
    }
    [Fact]
    public void TheDescriptorPoolTheOverlayStatesIsThePoolItCreates() {
        using var rig = new Rig();

        rig.Produce();

        Assert.Equal(
            expected: [UnifiedOverlayNode.DescriptorPoolSizes],
            actual: rig.Gpu.PoolsCreated
        );
    }
    [Fact]
    public void AnOverlayWhosePoolDoesNotFitTheHeapIsRefusedByNameBeforeItCreatesAnything() {
        using var rig = new Rig(
            countCalls: true,
            trackObjects: true
        );
        IGpuBindings bindings = rig.Gpu;
        var demand = UnifiedOverlayNode.DescriptorPoolSizes.HeapDescriptors;

        rig.Gpu.DescriptorHeap = new GpuDescriptorHeapBudget(capabilities: (GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: 3,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 0,
            shaderModel: "6.6",
            staticSamplerHeapSize: 0,
            viewHeapSize: 0
        ) with {
            ViewHeapSize = demand,
        }));

        // Another owner holds one view descriptor of a heap exactly the overlay's size.
        var other = bindings.CreatePool(sizes: new GpuDescriptorPoolSizes(
            CombinedImageSamplerCount: 0,
            MaxSets: 1,
            StorageBufferCount: 1,
            StorageImageCount: 0
        ));

        // The refusal is one more refused resource creation: the inner frame passes through and nothing throws.
        var refused = rig.Node.ProduceFrame(context: default);

        Assert.Equal(
            actual: (refused.ImageHandle, refused.ImageViewHandle),
            expected: (Rig.InnerImageHandle, Rig.InnerImageViewHandle)
        );
        Assert.StartsWith(
            actualString: rig.Node.ResourceRefusal,
            expectedStartString: $"[{GpuDescriptorHeapBudget.RefusalCode}] 'unified overlay' needs {demand} view descriptors in 1 pool(s) and is refused: "
        );
        Assert.Equal(
            actual: (rig.Gpu.Created.Count, rig.Gpu.Count(key: "IGpuImageFactory.Create"), rig.Gpu.Count(key: "IGpuPipelineFactory.Create(graphics)")),
            expected: (1, 0, 0)
        );

        // Frames that return no heap space try nothing again.
        for (var frame = 0; (frame < 3); frame++) {
            Assert.Equal(
                actual: rig.Node.ProduceFrame(context: default).ImageViewHandle,
                expected: Rig.InnerImageViewHandle
            );
        }

        Assert.Equal(
            actual: rig.Gpu.Admissions,
            expected: 1
        );

        // The other owner's release is the change a heap refusal waits for: the next frame creates the resources and
        // draws, with no device loss.
        bindings.DestroyPool(poolHandle: other);
        Assert.NotEqual(
            actual: rig.Node.ProduceFrame(context: default).ImageViewHandle,
            expected: Rig.InnerImageViewHandle
        );
        Assert.Null(@object: rig.Node.ResourceRefusal);
        Assert.Equal(
            actual: (rig.Gpu.Admissions, rig.Gpu.PoolsCreated[^1]),
            expected: (2, UnifiedOverlayNode.DescriptorPoolSizes)
        );
    }
    [Fact]
    public void ASteadyStateDrawnFrameAllocatesNothing() {
        using var rig = new Rig();
        var sample = new GpuWorkSample();

        void Frame() {
            rig.Produce();
            _ = rig.Node.Work.TryReadCompleted(sample: sample);
        }

        for (var warm = 0; (warm < 4); warm++) {
            Frame();
        }

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Frame));
        Assert.True(condition: (sample.Submission > 0L));
    }
    [Fact]
    public void EveryCreationOfTheOverlaysResourcesFaultedInTurnPresentsTheInnerFrameAndReleasesWhatWasCreated() {
        var expected = new Dictionary<GpuCreationKind, long>();

        using (var measured = new Rig(trackObjects: true)) {
            measured.Produce();

            foreach (var kind in GpuCreationFaults.Kinds) {
                expected[kind] = measured.Faults.SeenOf(kind: kind);
            }
        }

        var faulted = 0;

        foreach (var kind in GpuCreationFaults.Kinds) {
            for (var nth = 1; (nth <= expected[kind]); nth++) {
                using var rig = new Rig(trackObjects: true);

                rig.Faults.Arm(
                    kind: kind,
                    nth: nth
                );

                var refused = rig.Node.ProduceFrame(context: default);

                Assert.Equal(
                    actual: (refused.ImageHandle, refused.ImageViewHandle),
                    expected: (Rig.InnerImageHandle, Rig.InnerImageViewHandle)
                );
                Assert.StartsWith(
                    actualString: rig.Node.ResourceRefusal,
                    expectedStartString: $"[{GpuCreationFaults.RefusalCode}] The {GpuCreationFaults.NameOf(kind: kind)} creation {nth} "
                );
                Assert.All(
                    action: static created => Assert.Equal(
                        actual: created.DisposeCount,
                        expected: 1
                    ),
                    collection: rig.Gpu.Created
                );
                Assert.Equal(
                    actual: rig.Gpu.Memory.Held,
                    expected: 0L
                );

                // Nothing a frame changes could fix the creation, so later frames present the inner frame and create
                // nothing.
                var attempted = rig.Gpu.Created.Count;

                for (var frame = 0; (frame < 3); frame++) {
                    Assert.Equal(
                        actual: rig.Node.ProduceFrame(context: default).ImageViewHandle,
                        expected: Rig.InnerImageViewHandle
                    );
                }

                Assert.Equal(
                    actual: rig.Gpu.Created.Count,
                    expected: attempted
                );

                // A device loss is the change it waits for: the next frame creates the resources and draws.
                rig.Node.OnDeviceLost();
                Assert.Null(@object: rig.Node.ResourceRefusal);
                Assert.NotEqual(
                    actual: rig.Node.ProduceFrame(context: default).ImageViewHandle,
                    expected: Rig.InnerImageViewHandle
                );
                Assert.True(condition: (rig.Gpu.Memory.Held > 0L));
                faulted++;
            }
        }

        Assert.Equal(
            actual: faulted,
            expected: expected.Values.Sum()
        );
        Assert.True(condition: (faulted > 0));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern OverlayGlyphSdfPack CreateGlyphs(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf, int glyphCount);

    private sealed class Rig : IDisposable {
        private readonly CursorStore m_cursor = new();

        public Rig(bool countCalls = false, bool trackObjects = false) {
            var gpu = new FakeGpuDevice(
                countCalls: countCalls,
                reportVersion: 0,
                trackObjects: trackObjects
            );

            Gpu = gpu;
            m_cursor.Publish(frame: new OverlayCursorFrame(Seats: new[] {
                new OverlayCursorSeat(
                    Hover: false,
                    HoverLabel: "",
                    Role: default,
                    SizePx: 8f,
                    Viewport: new NormalizedRect(
                        Height: 1f,
                        Width: 1f,
                        X: 0f,
                        Y: 0f
                    ),
                    X: 16f,
                    Y: 16f
                ),
            }));
            Node = new UnifiedOverlayNode(
                capacity: new OverlayCapacity(
                    BindingBarMaxBanks: 0,
                    BindingBarMaxModifiers: 0,
                    BindingBarMaxSlotsPerBank: 0,
                    HudElementsPerPanel: 0,
                    HudElementsPerSeatPanel: 0,
                    HudPanels: 0,
                    HudSeatPanelsPerSeat: 0,
                    MarkerMaxChipsPerSeat: 0,
                    Seats: 1,
                    WheelMaxRings: 0,
                    WheelMaxSectorsPerRing: 0
                ),
                fragmentBytecode: new byte[] { 1 },
                glyphs: CreateGlyphs(
                    atlasCellHeight: 1,
                    atlasCellWidth: 1,
                    distanceRange: 1f,
                    glyphCount: 1,
                    packedSdf: [0u]
                ),
                height: 64,
                inner: new FixedRenderNode(surface: Surface.SameDeviceImage(
                    format: SurfaceFormat.R8G8B8A8Unorm,
                    height: 64,
                    imageHandle: InnerImageHandle,
                    imageViewHandle: InnerImageViewHandle,
                    width: 64
                )),
                deviceContext: new FaultingDevice(
                    faults: Faults,
                    gpu: gpu
                ),
                frameSources: new NoFrameSources(),
                sources: new UnifiedOverlaySources(
                    BindingBar: null,
                    Console: null,
                    Cursor: m_cursor,
                    FeedTick: null,
                    Toast: null
                ),
                theme: OverlayThemeValues.Zero with {
                    Chrome = OverlayThemeValues.Zero.Chrome with {
                        CursorAlpha = 1f,
                        CursorDotMaxHalf = 4f,
                        CursorDotRatio = 0.2f,
                    },
                },
                vertexBytecode: new byte[] { 1 },
                width: 64
            );
        }

        public const nint InnerImageHandle = 1;
        public const nint InnerImageViewHandle = 2;

        public GpuCreationFaults Faults { get; } = new();

        public FakeGpuDevice Gpu { get; }
        public UnifiedOverlayNode Node { get; }

        public void Dispose() => Node.Dispose();
        public void Produce() => _ = Node.ProduceFrame(context: default);
        public string Report() {
            var text = new StringBuilder();

            _ = GpuWorkReport.AppendCompleted(
                builder: text,
                sample: new GpuWorkSample(),
                source: Node.Work
            );

            return text.ToString();
        }
    }
    // The fake as a device context whose services pass through creation faults, as a backend's do.
    private sealed class FaultingDevice(FakeGpuDevice gpu, GpuCreationFaults faults) : IGpuDeviceContext {
        public long AdapterLuid => gpu.AdapterLuid;
        public GpuDeviceCapabilities? Capabilities => gpu.Capabilities;
        public GpuDeviceIdentity? Identity => gpu.Identity;
        public GpuMemoryProfile MemoryProfile => gpu.MemoryProfile;
        public GpuDeviceServices Services { get; } = GpuCreationFaults.Wrap(
            faults: faults,
            services: gpu.Services
        );

        public void WaitIdle() => gpu.WaitIdle();
    }
    private sealed class FixedRenderNode(Surface surface) : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new(
            Name: "overlay-work-inner",
            SurfaceId: SurfaceId.New()
        );

        public void Dispose() { }
        public Surface ProduceFrame(in FrameContext context) => surface;
    }
    private sealed class NoFrameSources : IOverlayFrameSources {
        public bool TryAcquire(int key, out GpuImageLease lease) {
            lease = default;

            return false;
        }
    }
}
