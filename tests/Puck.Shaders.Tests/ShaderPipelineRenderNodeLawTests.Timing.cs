using System.Buffers.Binary;
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
        gpu.WriteReadback = bytes => {
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

}
