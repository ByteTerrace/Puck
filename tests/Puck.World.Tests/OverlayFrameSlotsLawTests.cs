using System.Runtime.CompilerServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Overlays;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the fixed frame-slot table's host-owned lease lifecycle and visible capacity refusal.</summary>
public sealed class OverlayFrameSlotsLawTests {
    [Fact]
    public void AuthoringFrameSourceCapacityMatchesTheRuntimeSlotTable() => Assert.Equal(
        expected: WorldHudCapacity.MaxFrameSources,
        actual: OverlayFrameSlots.SlotCount
    );

    [Fact]
    public void PassThroughWaitsBeforeRetiringTheCurrentlyBoundLease() {
        var events = new List<string>();
        var slots = new OverlayFrameSlots(sources: new RecordingFrameSources(events: events));
        var fence = new RecordingFence(events: events);

        Assert.Equal(expected: 0, actual: slots.Bind(key: 41));

        slots.RetireAllAfter(fence: fence);

        Assert.Equal(expected: ["wait", "release:41"], actual: events);
        Assert.Equal(expected: 0, actual: slots.BoundCount);
    }

    [Fact]
    public void NoContentAfterBeginFrameWaitsThenRetiresPendingAndNewLeases() {
        var events = new List<string>();
        var slots = new OverlayFrameSlots(sources: new RecordingFrameSources(events: events));
        var fence = new RecordingFence(events: events);

        Assert.Equal(expected: 0, actual: slots.Bind(key: 11));
        slots.BeginFrame();
        Assert.Equal(expected: 0, actual: slots.Bind(key: 12));

        slots.RetireAllAfter(fence: fence);

        Assert.Equal(expected: ["wait", "release:11", "release:12"], actual: events);
        Assert.Equal(expected: 0, actual: slots.BoundCount);
    }

    [Fact]
    public void OverlayNodeEmptyInnerFrame_WaitsBeforeRetiringItsBoundLease() {
        var events = new List<string>();
        var node = BuildNode(
            events: events,
            inner: new FixedRenderNode(surface: default)
        );

        Assert.Equal(expected: 0, actual: FrameSlots(node: node).Bind(key: 31));
        FrameFence(node: node) = new RecordingFence(events: events);

        var result = node.ProduceFrame(context: default);

        Assert.True(condition: result.IsEmpty);
        Assert.Equal(expected: ["wait", "release:31"], actual: events);
        Assert.Equal(expected: 0, actual: FrameSlots(node: node).BoundCount);
    }

    [Fact]
    public void OverlayNodeNoContentAfterBeginFrame_WaitsBeforeRetiringItsPendingLease() {
        var events = new List<string>();
        var inner = Surface.SameDeviceImage(
            format: SurfaceFormat.R8G8B8A8Unorm,
            height: 1,
            imageHandle: 1,
            imageViewHandle: 2,
            width: 1
        );
        var node = BuildNode(
            events: events,
            inner: new FixedRenderNode(surface: inner)
        );

        Assert.Equal(expected: 0, actual: FrameSlots(node: node).Bind(key: 32));
        FrameFence(node: node) = new RecordingFence(events: events);

        var result = node.ProduceFrame(context: default);

        Assert.Equal(expected: inner, actual: result);
        Assert.Equal(expected: ["wait", "release:32"], actual: events);
        Assert.Equal(expected: 0, actual: FrameSlots(node: node).BoundCount);
    }

    [Theory]
    [InlineData(OverlayFrameExit.NoInnerFrame, false)]
    [InlineData(OverlayFrameExit.NoOverlayContent, false)]
    [InlineData(OverlayFrameExit.DeviceLost, true)]
    public void OnlyDeviceLossRetiresImmediately(OverlayFrameExit exit, bool expected) => Assert.Equal(
        expected: expected,
        actual: OverlayFrameRetirementPolicy.RetiresImmediately(exit: exit)
    );

    [Fact]
    public void OverlayNodeDeviceLoss_RetiresItsHeldLeaseWithoutWaitingOnTheFence() {
        var events = new List<string>();
        var node = BuildNode(
            events: events,
            inner: new FixedRenderNode(surface: default)
        );

        Assert.Equal(expected: 0, actual: FrameSlots(node: node).Bind(key: 51));
        FrameFence(node: node) = new RecordingFence(events: events);

        node.OnDeviceLost();

        Assert.Equal(expected: ["release:51"], actual: events);
        Assert.Equal(expected: 0, actual: FrameSlots(node: node).BoundCount);
    }

    [Fact]
    public void DeviceLossRetiresEveryHeldLeaseWithoutWaitingOnTheFence() {
        var events = new List<string>();
        var slots = new OverlayFrameSlots(sources: new RecordingFrameSources(events: events));

        Assert.Equal(expected: 0, actual: slots.Bind(key: 21));
        slots.BeginFrame();
        Assert.Equal(expected: 0, actual: slots.Bind(key: 22));

        slots.RetireAll();

        Assert.Equal(expected: ["release:21", "release:22"], actual: events);
        Assert.Equal(expected: 0, actual: slots.BoundCount);
    }

    [Fact]
    public void ADistinctNinthSourceSetsTheCapacitySignalWithoutAcquiringIt() {
        var events = new List<string>();
        var sources = new RecordingFrameSources(events: events);
        var slots = new OverlayFrameSlots(sources: sources);

        for (var key = 0; (key < OverlayFrameSlots.SlotCount); key++) {
            Assert.Equal(expected: key, actual: slots.Bind(key: key));
        }

        Assert.Equal(expected: -1, actual: slots.Bind(key: OverlayFrameSlots.SlotCount));
        Assert.True(condition: slots.CapacityExceeded);
        Assert.Equal(expected: OverlayFrameSlots.SlotCount, actual: sources.AcquisitionCount);

        slots.BeginFrame();

        Assert.False(condition: slots.CapacityExceeded);
        slots.RetireAll();
    }

    private static UnifiedOverlayNode BuildNode(List<string> events, IRenderNode inner) => new(
        capacity: new OverlayCapacity(
            Seats: 0,
            HudPanels: 0,
            HudElementsPerPanel: 0,
            HudSeatPanelsPerSeat: 0,
            HudElementsPerSeatPanel: 0,
            BindingBarMaxBanks: 0,
            BindingBarMaxSlotsPerBank: 0,
            BindingBarMaxModifiers: 0,
            MarkerMaxChipsPerSeat: 0,
            WheelMaxRings: 0,
            WheelMaxSectorsPerRing: 0
        ),
        fragmentBytecode: ReadOnlyMemory<byte>.Empty,
        glyphs: CreateGlyphs(
            atlasCellWidth: 1,
            atlasCellHeight: 1,
            distanceRange: 1f,
            packedSdf: [0u],
            glyphCount: 1
        ),
        height: 1,
        inner: inner,
        services: new OverlayServices {
            BytecodeExtension = ".test",
            CommandRecorder = null!,
            CreateRenderTarget = static (_, _) => null!,
            DescriptorAllocator = null!,
            DeviceContext = new FixedDeviceContext(),
            FrameSources = new RecordingFrameSources(events: events),
            PipelineFactory = null!,
            QueueSubmitter = null!,
            ShaderModuleFactory = null!,
            StorageBufferBinding = 0,
            StorageBufferFactory = null!,
            SurfaceTransferFactory = null!,
            VertexBufferFactory = null!,
        },
        sources: new UnifiedOverlaySources(
            Console: null,
            BindingBar: null,
            Toast: null,
            FeedTick: null
        ),
        vertexBytecode: ReadOnlyMemory<byte>.Empty,
        width: 1
    );

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern OverlayGlyphSdfPack CreateGlyphs(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf, int glyphCount);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_frameSlots")]
    private static extern ref OverlayFrameSlots FrameSlots(UnifiedOverlayNode node);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_frameFence")]
    private static extern ref IGpuSubmissionFence? FrameFence(UnifiedOverlayNode node);

    private sealed class RecordingFence(List<string> events) : IGpuSubmissionFence {
        public void Dispose() { }

        public void Wait() => events.Add(item: "wait");
    }

    private sealed class RecordingFrameSources(List<string> events) : IOverlayFrameSources {
        public int AcquisitionCount { get; private set; }

        public bool TryAcquire(int key, out OverlayFrameLease lease) {
            AcquisitionCount++;
            lease = new OverlayFrameLease(
                ImageViewHandle: key + 1,
                Release: token => events.Add(item: $"release:{token}"),
                ReleaseToken: key
            );

            return true;
        }
    }

    // Only ReleaseGpuResources' unconditional DeviceHandle read needs a real instance (the null-service fields
    // this suite's nodes carry are never exercised by an early-exit or device-loss path).
    private sealed class FixedDeviceContext : IGpuDeviceContext {
        public long AdapterLuid => 0L;
        public nint DeviceHandle => 0;

        public void WaitIdle() { }
    }

    private sealed class FixedRenderNode(Surface surface) : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new(
            Name: "overlay-law-inner",
            SurfaceId: SurfaceId.New()
        );

        public void Dispose() { }

        public Surface ProduceFrame(in FrameContext context) => surface;
    }
}
