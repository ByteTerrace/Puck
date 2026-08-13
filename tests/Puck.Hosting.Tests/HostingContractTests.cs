using Puck.Abstractions.Capture;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Presentation;

namespace Puck.Hosting.Tests;

public sealed class HostingContractTests {
    [Fact]
    public void CapturingRenderNodeKeepsFractionalCadence() {
        using var inner = new TestRenderNode(cpuPixels: true);
        using var sink = new CountingCaptureSink();
        using var node = new CapturingRenderNode(
            inner: inner,
            sink: sink,
            options: new CaptureOptions { FrameRate = 24, SourceFrameRate = 60 }
        );
        var context = FrameContext();

        for (var frame = 0; (frame < 60); ++frame) {
            _ = node.ProduceFrame(context: in context);
        }

        Assert.Equal(expected: 24, actual: sink.Count);
    }

    [Fact]
    public void CapturingRenderNodeContainsCaptureCallbackFaults() {
        using var gateInner = new TestRenderNode(cpuPixels: true);
        using var gateSink = new CountingCaptureSink();
        var gateCalls = 0;
        using var gateNode = new CapturingRenderNode(
            inner: gateInner,
            sink: gateSink,
            options: new CaptureOptions(),
            captureGate: () => {
                ++gateCalls;

                throw new InvalidOperationException(message: "gate fault");
            }
        );
        var context = FrameContext();

        _ = gateNode.ProduceFrame(context: in context);
        _ = gateNode.ProduceFrame(context: in context);

        Assert.Equal(expected: 1, actual: gateCalls);
        Assert.Equal(expected: 0, actual: gateSink.Count);

        using var readbackInner = new TestRenderNode(cpuPixels: false);
        using var readbackSink = new CountingCaptureSink();
        var readbackCalls = 0;
        using var readbackNode = new CapturingRenderNode(
            inner: readbackInner,
            sink: readbackSink,
            options: new CaptureOptions(),
            cpuReadback: () => {
                ++readbackCalls;

                throw new InvalidOperationException(message: "readback fault");
            }
        );

        _ = readbackNode.ProduceFrame(context: in context);
        _ = readbackNode.ProduceFrame(context: in context);

        Assert.Equal(expected: 1, actual: readbackCalls);
        Assert.Equal(expected: 0, actual: readbackSink.Count);
    }

    [Theory]
    [InlineData(0, 60, 0)]
    [InlineData(30, 0, 0)]
    [InlineData(30, 60, -1)]
    public void CapturingRenderNodeRejectsInvalidCadence(int frameRate, int sourceFrameRate, int maxFrames) {
        using var inner = new TestRenderNode(cpuPixels: true);
        using var sink = new CountingCaptureSink();
        var options = new CaptureOptions {
            FrameRate = frameRate,
            SourceFrameRate = sourceFrameRate,
            MaxFrames = maxFrames,
        };

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new CapturingRenderNode(
            inner: inner,
            sink: sink,
            options: options
        ));
    }

    [Fact]
    public void CapabilityRevocationCascadesThroughIrrevocableSubgrant() {
        ITestCapability capability = new TestCapability();
        var root = new HostContext(
            capabilities: new Dictionary<Type, object>(),
            heldCapabilities: new Dictionary<Type, object> { [typeof(ITestCapability)] = capability }
        );
        var parentGrants = new HeldCapabilityGrants();
        var parentTakeBack = parentGrants.Grant<ITestCapability>(grantor: root);
        var parent = new HostContext(capabilities: new Dictionary<Type, object>(), heldGrants: parentGrants);
        var childGrants = new HeldCapabilityGrants();
        var childTakeBack = childGrants.Grant<ITestCapability>(grantor: parent, revocable: false);
        var child = new HostContext(capabilities: new Dictionary<Type, object>(), heldGrants: childGrants);

        Assert.NotNull(@object: parentTakeBack);
        Assert.Null(@object: childTakeBack);
        Assert.True(condition: child.HoldsCapability<ITestCapability>(capability: out _));

        parentTakeBack.Revoke();

        Assert.True(condition: parentTakeBack.IsRevoked);
        Assert.False(condition: parent.HoldsCapability<ITestCapability>(capability: out _));
        Assert.False(condition: child.HoldsCapability<ITestCapability>(capability: out _));
    }

    [Fact]
    public void QueuedMachineWorkerRejectsLoadAfterDisposal() {
        using var core = new TestQueuedCore();
        var worker = new QueuedMachineWorker(
            width: 1,
            height: 1,
            maximumPendingSteps: 1,
            workerName: "hosting-contract-test"
        );

        worker.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(testCode: () => worker.Load(core: core));
        Assert.False(condition: worker.IsAssigned);
        Assert.Equal(expected: 0, actual: core.DisposeCount);
    }

    [Fact]
    public void OsTimeCorrelatorRejectsZeroAndDefaultFrequency() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => OsTimeCorrelator.Pin(
            osReference: 0U,
            engineReference: 0UL,
            osFrequency: 0UL
        ));
        var correlator = default(OsTimeCorrelator);

        _ = Assert.Throws<InvalidOperationException>(testCode: () => correlator.ToEngineTicks(
            osStamp: 0U,
            engineCeiling: 0UL
        ));
    }

    private static FrameContext FrameContext() => new(
        Host: HostContext.Empty,
        ElapsedTicks: 0UL,
        DeltaTicks: 0UL,
        FrameDeltaTicks: 0UL,
        AccumulatorTicks: 0UL,
        StepTicks: 1UL,
        TargetWidth: 1U,
        TargetHeight: 1U
    );

    private interface ITestCapability { }

    private sealed class TestCapability : ITestCapability { }

    private sealed class CountingCaptureSink : ICaptureSink {
        public int Count { get; private set; }

        public void Consume(in CaptureFrame frame) => ++Count;
        public void Dispose() { }
    }

    private sealed class TestRenderNode(bool cpuPixels) : IRenderNode {
        private readonly byte[] m_pixels = [0, 0, 0, 255];

        public NodeDescriptor Descriptor => new(Name: "test", SurfaceId: default);

        public Surface ProduceFrame(in FrameContext context) => (cpuPixels
            ? Surface.CpuPixels(pixels: m_pixels, width: 1U, height: 1U, format: SurfaceFormat.R8G8B8A8Unorm)
            : Surface.SameDeviceImage(imageViewHandle: 1, width: 1U, height: 1U, format: SurfaceFormat.R8G8B8A8Unorm));
        public void Dispose() { }
    }

    private sealed class TestQueuedCore : IQueuedMachineCore {
        private readonly uint[] m_framebuffer = [0U];

        public int DisposeCount { get; private set; }
        public ulong CyclesPerSecond => 1UL;
        public long CycleCount => 0L;
        public long NativeFrameIndex => 0L;
        public ReadOnlySpan<uint> Framebuffer => m_framebuffer;

        public void ConfigureAudio(int sampleRate) { }
        public int DrainAudioSamples(Span<short> destination) => 0;
        public void FlushSave(bool force) { }
        public int CaptureState(ref byte[] buffer) => 0;
        public void RestoreState(byte[] buffer, int length) { }
        public void ApplyInput(in MachinePadState input) { }
        public void RunCycles(long cycles) { }
        public ITimeTravelLookahead<MachinePadState> CreateLookahead() => throw new NotSupportedException();
        public void Dispose() => ++DisposeCount;
    }
}
