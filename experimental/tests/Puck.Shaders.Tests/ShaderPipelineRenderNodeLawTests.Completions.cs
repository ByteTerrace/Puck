using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class ShaderPipelineRenderNodeLawTests {
    [Fact]
    public void APausedInstallStillOwesTheEarlierRendersCompletion() {
        var gpu = new FakeGpuDevice(holdFences: true);
        using var node = new ShaderPipelineRenderNode(name: "completions", deviceContext: gpu,
            pipelines: new GpuPassPipelineCache(), hostsOnDirectX: false, width: Extent, height: Extent);

        node.Swap(pipeline: CanaryPipeline(canary: "pipeline-feedback", fileName: "feedback.graph.json"));
        _ = node.ProduceUntilInstalled();
        var fence = Assert.IsType<FakeGpuDevice.Fence>(@object: gpu.LastSubmittedFence);

        Assert.Equal(expected: default, actual: node.TakeCompletions());
        node.Paused = true;
        node.Resize(height: (Extent * 2), width: (Extent * 2));
        TestLiveness.Until(step: () => {
            _ = Produce(node: node);
            return (node.Extent == ((Extent * 2), (Extent * 2)));
        });

        // Installation withdraws counters and renders nothing, but the earlier render still completes on the GPU.
        Assert.Equal(expected: 1UL, actual: node.FrameCounter);
        Assert.False(condition: node.TryReadCompleted(sample: new GpuWorkSample()));
        Assert.True(condition: node.OwesReadbacks);
        fence.Completed = true;
        node.PollReadbacks();
        Assert.Equal(expected: new ShaderPipelineCompletions(Grid: 1d, Renders: 1), actual: node.TakeCompletions());
        Assert.False(condition: node.OwesReadbacks);
        node.PollReadbacks();
        Assert.Equal(expected: default, actual: node.TakeCompletions());
    }
    [Fact]
    public void ALostRenderIsNeverCountedWhenItsSuccessorCompletes() {
        var gpu = new FakeGpuDevice(holdFences: true);
        using var node = new ShaderPipelineRenderNode(name: "completions", deviceContext: gpu,
            pipelines: new GpuPassPipelineCache(), hostsOnDirectX: false, width: Extent, height: Extent);

        node.Swap(pipeline: CanaryPipeline(canary: "pipeline-feedback", fileName: "feedback.graph.json"));
        _ = node.ProduceUntilInstalled();
        Assert.Equal(expected: default, actual: node.TakeCompletions());
        node.OnDeviceLost();
        Assert.False(condition: node.OwesReadbacks);
        _ = node.ProduceUntilInstalled();
        node.Paused = true;
        Assert.Equal(expected: default, actual: node.TakeCompletions());
        Assert.IsType<FakeGpuDevice.Fence>(@object: gpu.LastSubmittedFence).Completed = true;
        node.PollReadbacks();

        // Submission identities continue across device loss; the missing identity is no completed render.
        Assert.Equal(expected: new ShaderPipelineCompletions(Grid: 1d, Renders: 1), actual: node.TakeCompletions());
        Assert.Equal(expected: default, actual: node.TakeCompletions());
        Assert.False(condition: node.OwesReadbacks);
    }
}
