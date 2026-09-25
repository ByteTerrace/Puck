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
/// one it creates, that a pool the device's heap cannot admit is refused by name before anything is created, and that a
/// steady-state drawn frame allocates nothing.
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
        using var rig = new Rig(countCalls: true);
        var demand = UnifiedOverlayNode.DescriptorPoolSizes.HeapDescriptors;

        GpuDescriptorHeapBudget Heap(uint views) => new(capabilities: (GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: 3,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 0,
            shaderModel: "6.6",
            viewHeapSize: 0
        ) with {
            ViewHeapSize = views,
        }));

        rig.Gpu.DescriptorHeap = Heap(views: (demand - 1U));

        var refusal = Assert.Throws<InvalidOperationException>(testCode: rig.Produce);

        Assert.StartsWith(
            actualString: refusal.Message,
            expectedStartString: $"[{GpuDescriptorHeapBudget.RefusalCode}] 'unified overlay' needs {demand} view descriptors in 1 pool(s) and is refused: "
        );
        Assert.Empty(collection: rig.Gpu.PoolsCreated);
        Assert.Equal(
            actual: (rig.Gpu.Count(key: "IGpuImageFactory.Create"), rig.Gpu.Count(key: "IGpuPipelineFactory.Create(graphics)")),
            expected: (0, 0)
        );

        rig.Gpu.DescriptorHeap = Heap(views: demand);
        rig.Produce();

        Assert.Equal(
            actual: rig.Gpu.PoolsCreated,
            expected: [UnifiedOverlayNode.DescriptorPoolSizes]
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

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern OverlayGlyphSdfPack CreateGlyphs(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf, int glyphCount);

    private sealed class Rig : IDisposable {
        private readonly CursorStore m_cursor = new();

        public Rig(bool countCalls = false) {
            var gpu = new FakeGpuDevice(
                countCalls: countCalls,
                reportVersion: 0
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
                    imageHandle: 1,
                    imageViewHandle: 2,
                    width: 64
                )),
                deviceContext: gpu,
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
