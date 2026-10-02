using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class ShaderPipelineRenderNodeLawTests {
    [Fact]
    public void GpuTimingIsDemandedPairedFencedAndReleasedOnLoss() {
        var gpu = new FakeGpuDevice(countCalls: true, holdFences: true, trackObjects: true);
        using var node = new ShaderPipelineRenderNode(name: "timing", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: Extent, height: Extent);

        node.Swap(pipeline: CanaryPipeline(canary: "pipeline-feedback", fileName: "feedback.graph.json"));
        _ = node.ProduceUntilInstalled();
        Produce(frames: WarmFrames, node: node);
        Assert.Equal(expected: 0, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampFactory.Create"));
        Assert.Equal(expected: 0, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampPool.Write"));
        Assert.Empty(collection: node.Timings.ToArray());
        var reads = 0;

        gpu.OnReadback = bytes => { if (bytes == 48) { reads++; } };
        gpu.WriteReadback = (_, bytes) => {
            for (var offset = 0; ((offset + 16) <= bytes.Length); offset += 16) {
                BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes[offset..], value: 0xFFFFFFF0);
                BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes[(offset + 8)..], value: 0x10);
            }
        };
        var offBytes = node.OwnedBytes;

        Assert.Equal(expected: 0UL, actual: node.TimingReadbackBytes);
        node.TimingEnabled = true;
        _ = Produce(node: node);
        Assert.Equal(expected: 1, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampFactory.Create"));
        Assert.Equal(expected: 6, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampPool.Write"));
        Assert.Equal(expected: 3, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampPool.Reset"));
        Assert.Equal(expected: 3, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampPool.Resolve"));
        Assert.Equal(expected: 48UL, actual: node.TimingReadbackBytes);
        Assert.Equal(expected: (offBytes + 48UL), actual: node.OwnedBytes);
        Assert.True(condition: (node.TimingCpuBytes >= 48UL));
        node.Paused = true;
        _ = Produce(node: node);
        Assert.Equal(actual: reads, expected: 0);
        ((FakeGpuDevice.Fence)gpu.LastSubmittedFence!).Completed = true;
        _ = Produce(node: node);
        Assert.Equal(actual: reads, expected: 1);
        Assert.All(collection: node.Timings.ToArray(), action: entry => {
            Assert.Equal(expected: 1, actual: entry.Samples);
            Assert.Equal(expected: (64.0 / 1_000_000), actual: entry.Milliseconds);
        });
        node.OnDeviceLost();
        Assert.Empty(collection: node.Timings.ToArray());
        Assert.Equal(expected: 1, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampPool.Dispose"));
        Assert.All(collection: gpu.Created.Where(predicate: item => (item.Kind == "timestamp pool")), action: item => Assert.Equal(expected: 1, actual: item.DisposeCount));
        Assert.Equal(expected: 0UL, actual: node.TimingReadbackBytes);
        Assert.Equal(expected: 0UL, actual: node.TimingCpuBytes);
        node.TimingEnabled = false;
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void LatestGpuTimeBelongsToTheNewestSubmissionAcrossSlotWrap(int firstSlot) {
        var gpu = new FakeGpuDevice(holdFences: true);
        using var node = new ShaderPipelineRenderNode(name: "timing", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: Extent, height: Extent, inFlightFrames: InFlight);

        node.Swap(pipeline: CanaryPipeline(canary: "pipeline-feedback", fileName: "feedback.graph.json"));
        _ = node.ProduceUntilInstalled();
        Produce(frames: WarmFrames, node: node);
        while ((node.FrameCounter % InFlight) != ((ulong)firstSlot)) {
            _ = Produce(node: node);
        }
        var durations = new ulong[InFlight];
        var fences = new List<FakeGpuDevice.Fence>();

        gpu.WriteReadback = (name, bytes) => {
            if (name.Part != "timing") { return; }
            for (var offset = 0; ((offset + 16) <= bytes.Length); offset += 16) {
                BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes[offset..], value: 0);
                BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes[(offset + 8)..], value: durations[name.Index]);
            }
        };
        node.TimingEnabled = true;
        for (var frame = 0U; (frame < InFlight); frame++) {
            durations[(node.FrameCounter % InFlight)] = ((frame + 1UL) * 1_000_000UL);
            _ = Produce(node: node);
            fences.Add(item: Assert.IsType<FakeGpuDevice.Fence>(@object: gpu.LastSubmittedFence));
        }
        Assert.Equal(expected: 0L, actual: node.TimingFrames);
        node.Paused = true;
        foreach (var fence in fences) { fence.Completed = true; }
        _ = Produce(node: node);

        // Three passes, each taking six milliseconds in the newest submission, whatever slot it occupied.
        Assert.Equal(expected: 18d, actual: node.LatestTimingMilliseconds);
        Assert.Equal(expected: 3L, actual: node.TimingFrames);
        Assert.All(collection: node.Timings.ToArray(), action: timing => Assert.Equal(expected: 4d, actual: timing.Milliseconds));
        // The latest time names the newest completed submission, and the grid the node rendered it at: the output's own,
        // since no package of the graph sets another.
        var completed = new GpuWorkSample();

        Assert.True(condition: node.TryReadCompleted(sample: completed));
        Assert.Equal(expected: completed.Submission, actual: node.LatestTimingSubmission);
        Assert.True(condition: node.TryGetRenderGrid(grid: out var grid, submission: node.LatestTimingSubmission));
        Assert.Equal(actual: grid, expected: 1d);
        _ = Produce(node: node);
        Assert.Equal(expected: 3L, actual: node.TimingFrames);
    }
    [Fact]
    public void TimingDisableImmediatelyHidesAndFenceRetiresItsEarlierEpoch() {
        var gpu = new FakeGpuDevice(countCalls: true, holdFences: true, trackObjects: true);
        using var node = new ShaderPipelineRenderNode(name: "timing", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: Extent, height: Extent);

        node.Swap(pipeline: CanaryPipeline(canary: "pipeline-feedback", fileName: "feedback.graph.json"));
        _ = node.ProduceUntilInstalled();
        Produce(frames: WarmFrames, node: node);
        node.TimingEnabled = true;
        _ = Produce(node: node);
        var pending = ((FakeGpuDevice.Fence)gpu.LastSubmittedFence!);

        node.Paused = true;
        node.TimingEnabled = false;
        Assert.Empty(collection: node.Timings.ToArray());
        Assert.Equal(expected: 0, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampPool.Dispose"));
        Assert.Equal(expected: 48UL, actual: node.TimingReadbackBytes);
        node.TimingEnabled = true;
        node.Paused = false;
        _ = Produce(node: node);
        var latest = ((FakeGpuDevice.Fence)gpu.LastSubmittedFence!);

        node.Paused = true;
        var reads = 0;

        gpu.OnReadback = _ => reads++;
        pending.Completed = true;
        _ = Produce(node: node);
        Assert.Empty(collection: node.Timings.ToArray());
        Assert.Equal(actual: reads, expected: 0);
        latest.Completed = true;
        node.TimingEnabled = false;
        Assert.Equal(expected: 0UL, actual: node.TimingReadbackBytes);
        Assert.Equal(expected: 2, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampPool.Dispose"));
    }
    // Timing is observational: a pool or readback the device will not create refuses timing by name, the graph keeps
    // rendering, nothing is tried again on a later frame, and what the failed slot created is released. Enabling again is
    // a new demand, which tries once more.
    [InlineData(GpuCreationKind.TimestampPool)]
    [InlineData(GpuCreationKind.Buffer)]
    [Theory]
    public void GpuTimingCreationFaultRefusesTimingByNameAndFramesKeepRendering(GpuCreationKind kind) {
        var gpu = new FakeGpuDevice(countCalls: true, trackObjects: true);
        var faults = new GpuCreationFaults();
        using var node = new ShaderPipelineRenderNode(name: "timing", deviceContext: new FaultingDevice(faults: faults, gpu: gpu),
            pipelines: new GpuPassPipelineCache(), hostsOnDirectX: false, width: Extent, height: Extent);

        node.Swap(pipeline: CanaryPipeline(canary: "pipeline-feedback", fileName: "feedback.graph.json"));
        _ = node.ProduceUntilInstalled();
        Produce(frames: WarmFrames, node: node);
        faults.Arm(kind: kind);
        var seen = faults.SeenOf(kind: kind);
        var frames = node.FrameCounter;

        node.TimingEnabled = true;
        var surface = Produce(node: node);

        Assert.False(condition: surface.IsEmpty);
        Assert.False(condition: faults.TryGetArmed(kind: kind, remaining: out _));
        Assert.Contains(expectedSubstring: GpuCreationFaults.RefusalCode, actualString: node.TimingRefusal);
        Assert.Empty(collection: node.Timings.ToArray());
        Produce(frames: WarmFrames, node: node);
        Assert.Equal(expected: ((frames + 1UL) + WarmFrames), actual: node.FrameCounter);
        Assert.Equal(expected: (seen + 1), actual: faults.SeenOf(kind: kind));
        Assert.Equal(expected: 0, actual: gpu.Calls.GetValueOrDefault(key: "IGpuTimestampPool.Write"));
        Assert.All(collection: gpu.Created.Where(predicate: static item => (item.Kind == "timestamp pool")),
            action: static item => Assert.Equal(expected: 1, actual: item.DisposeCount));
        Assert.Equal(expected: 0UL, actual: node.TimingReadbackBytes);

        node.TimingEnabled = false;
        node.TimingEnabled = true;
        _ = Produce(node: node);
        Assert.Null(@object: node.TimingRefusal);
        Assert.True(condition: (gpu.Calls.GetValueOrDefault(key: "IGpuTimestampPool.Write") > 0));
    }

}
