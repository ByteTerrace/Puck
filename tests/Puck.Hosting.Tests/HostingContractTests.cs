using Puck.Abstractions.Capture;
using Puck.Abstractions.Presentation;

namespace Puck.Hosting.Tests;

public sealed class HostingContractTests {
    [Fact]
    public void ExternalClocksAcceptZeroTimestampAndUnregister() {
        var clock = new ExternalPresentClock();

        clock.Publish(arrivalTimestamp: 0, frameVersion: 7);
        Assert.True(condition: clock.TryRead(arrivalTimestamp: out var timestamp, frameVersion: out var version));
        Assert.Equal(actual: timestamp, expected: 0);
        Assert.Equal(actual: version, expected: 7);

        var registry = new ExternalClockRegistry();
        var source = registry.RegisterSource(sourceId: "camera:0");

        source.Publish(arrivalTimestamp: 11, frameVersion: 1);
        Assert.True(condition: registry.PacerClock.TryRead(arrivalTimestamp: out timestamp, frameVersion: out version));
        source.Dispose();
        Assert.Empty(collection: registry.SourceIds);
        _ = Assert.Throws<ObjectDisposedException>(testCode: () => source.Publish(arrivalTimestamp: 12, frameVersion: 2));
    }
    [Fact]
    public void FrameCaptureControllerKeepsFractionalCadence() {
        using var sink = new CountingCaptureSink();
        using var controller = new FrameCaptureController();

        controller.Arm(
            sink: sink,
            options: new CaptureOptions { FrameRate = 24 }
        );
        var surface = Surface.CpuPixels(
            format: SurfaceFormat.R8G8B8A8Unorm,
            height: 1U,
            pixels: new byte[4],
            width: 1U
        );

        for (var frame = 0; (frame < 60); ++frame) {
            var context = FrameContext(elapsedTicks: (((ulong)frame) * EngineTicks.PerRate(ratePerSecond: 60U)));

            controller.Capture(
                context: in context,
                readback: null,
                surface: surface
            );
        }

        Assert.Equal(expected: 24, actual: sink.Count);
    }
    [Fact]
    public void FrameCaptureControllerContainsReadbackFaults() {
        using var sink = new CountingCaptureSink();
        using var controller = new FrameCaptureController();
        var readback = new FaultingReadback();
        var surface = Surface.SameDeviceImage(
            format: SurfaceFormat.R8G8B8A8Unorm,
            height: 1U,
            imageHandle: 1,
            imageViewHandle: 2,
            width: 1U
        );
        var context = FrameContext();

        controller.Arm(
            sink: sink,
            options: new CaptureOptions()
        );
        controller.Capture(context: in context, readback: readback, surface: surface);
        controller.Capture(context: in context, readback: readback, surface: surface);

        Assert.Equal(expected: 1, actual: readback.CallCount);
        Assert.Equal(expected: 0, actual: sink.Count);
        Assert.False(condition: controller.WantsFrames);
        Assert.IsType<InvalidOperationException>(@object: controller.Fault);
        Assert.Same(expected: sink, actual: controller.Disarm());
    }
    [InlineData(0, 0)]
    [InlineData(30, -1)]
    [Theory]
    public void FrameCaptureControllerRejectsInvalidCadence(int frameRate, int maxFrames) {
        using var sink = new CountingCaptureSink();
        using var controller = new FrameCaptureController();
        var options = new CaptureOptions {
            FrameRate = frameRate,
            MaxFrames = maxFrames,
        };

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => controller.Arm(
            options: options,
            sink: sink
        ));
    }
    [Fact]
    public void FrameCaptureControllerReadsTheExactGpuSurfaceAndResetsSessionIndex() {
        using var firstSink = new CountingCaptureSink();
        using var secondSink = new CountingCaptureSink();
        using var controller = new FrameCaptureController();
        var readback = new RecordingReadback();
        var surface = Surface.SameDeviceImage(
            format: SurfaceFormat.B8G8R8A8Unorm,
            height: 1U,
            imageHandle: 11,
            imageViewHandle: 12,
            width: 1U
        );
        var context = FrameContext(elapsedTicks: 42UL);

        controller.Arm(
            sink: firstSink,
            options: new CaptureOptions { MaxFrames = 1 }
        );
        controller.Capture(context: in context, readback: readback, surface: surface);

        Assert.False(condition: controller.WantsFrames);
        Assert.Equal(expected: 0L, actual: firstSink.LastFrame.FrameIndex);
        Assert.Equal(expected: 42UL, actual: firstSink.LastFrame.TimestampTicks);
        Assert.Equal(expected: surface, actual: readback.LastSurface);
        Assert.Same(expected: firstSink, actual: controller.Disarm());

        controller.Arm(
            sink: secondSink,
            options: new CaptureOptions { MaxFrames = 1 }
        );
        controller.Capture(context: in context, readback: readback, surface: surface);

        Assert.Equal(expected: 0L, actual: secondSink.LastFrame.FrameIndex);
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
    public void OsTimeCorrelatorRejectsZeroAndDefaultFrequency() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => OsTimeCorrelator.Pin(
            engineReference: 0UL,
            osFrequency: 0UL,
            osReference: 0U
        ));
        var correlator = default(OsTimeCorrelator);

        _ = Assert.Throws<InvalidOperationException>(testCode: () => correlator.ToEngineTicks(
            engineCeiling: 0UL,
            osStamp: 0U
        ));
    }

    private static FrameContext FrameContext(ulong elapsedTicks = 0UL) => new(
        Host: HostContext.Empty,
        ElapsedTicks: elapsedTicks,
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
        public CaptureFrame LastFrame { get; private set; }

        public void Consume(in CaptureFrame frame) {
            LastFrame = frame;
            ++Count;
        }
        public void Dispose() { }
    }
    private sealed class FaultingReadback : IPresentSurfaceReadback {
        public int CallCount { get; private set; }

        public Surface ReadSurface(Surface surface) {
            ++CallCount;

            throw new InvalidOperationException(message: "readback fault");
        }
    }
    private sealed class RecordingReadback : IPresentSurfaceReadback {
        private readonly byte[] m_pixels = [0, 0, 0, 255];

        public Surface LastSurface { get; private set; }

        public Surface ReadSurface(Surface surface) {
            LastSurface = surface;

            return Surface.CpuPixels(
                pixels: m_pixels,
                width: surface.Width,
                height: surface.Height,
                format: surface.Format
            );
        }
    }
}
