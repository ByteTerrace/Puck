using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Descriptor-pool laws for <see cref="ShaderPipelineRenderNode"/>. The node states the pools a plan needs before
/// anything is allocated, and the fake records, independently, the size of every pool the node actually creates. The
/// two agree, pool by pool and in creation order, for the graph alone and with a float preview: one pool for all the
/// graph's passes and in-flight slots, and one for the preview, so a heap that admits the statement admits what the
/// node requests. Over a fake device heap, as on Direct3D 12, a candidate the heap cannot hold beside the installed
/// graph is refused by name at install and nothing grows; one that fits exactly installs, and the replaced graph's range
/// is the one the next candidate receives once the replaced graph retires.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    // The view descriptors a plan's pools occupy in a device heap.
    private static uint HeapDemand(CompiledShaderPipeline pipeline) => ((uint)ShaderPipelineRenderNode.DescriptorPools(
        inFlight: InFlight,
        packages: new RenderGraphPackageRecorders(),
        plan: pipeline.Plan,
        preview: false
    ).Sum(selector: static pool => pool.HeapDescriptors));
    // A device heap of exactly this many view descriptors.
    private static GpuDescriptorHeapBudget DeviceHeap(uint views) => new(capabilities: (GpuDeviceCapabilities.FromDirectX(
        resourceBindingTier: 3,
        rootSignatureVersion: "1.1",
        samplerHeapSize: 0,
        shaderModel: "6.6",
        staticSamplerHeapSize: 0,
        viewHeapSize: 0
    ) with {
        ViewHeapSize = views,
    }));

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void TheDescriptorPoolsANodeStatesAreThePoolsItCreatesOneForTheGraphAndOneForThePreview(bool floatOutput) {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            floatOutput: floatOutput,
            gpu: gpu
        );
        var stated = ShaderPipelineRenderNode.DescriptorPools(
            inFlight: InFlight,
            packages: new RenderGraphPackageRecorders(),
            plan: node.Plan!,
            preview: floatOutput
        );

        Assert.Equal(
            expected: stated,
            actual: gpu.DescriptorPools
        );
        Assert.Equal(
            actual: (Pools: stated.Count, GraphSets: stated[0].MaxSets),
            expected: (Pools: (floatOutput ? 2 : 1), GraphSets: (((uint)node.Plan!.Passes.Count) * InFlight))
        );
    }
    [Fact]
    public void ACandidateTheDeviceHeapCannotHoldIsRefusedByNameAtInstallAndNothingGrows() {
        var installed = Feedback();
        var candidate = Feedback(historyFormat: "R32G32B32A32Float");
        var heap = DeviceHeap(views: ((HeapDemand(pipeline: installed) + HeapDemand(pipeline: candidate)) - 1U));
        var gpu = new FakePipelineGpu {
            DescriptorHeap = heap,
        };
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: installed
        );
        var free = heap.FreeViewDescriptors;
        var pools = gpu.DescriptorPools.Count;
        var liveBytes = gpu.LiveBytes;

        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: candidate
        );

        Assert.IsType<InvalidDataException>(@object: node.LastSwapError);
        Assert.StartsWith(
            actualString: node.LastSwapError!.Message,
            expectedStartString: $"[{GpuDescriptorHeapBudget.RefusalCode}] 'shader pipeline feedback' needs {HeapDemand(pipeline: candidate)} view descriptors in 1 pool(s) and is refused: "
        );
        Assert.False(condition: (node.HasPendingCandidate || node.IsBuildingCandidate));

        // The installed graph keeps presenting, and the candidate allocated nothing: no pool, no range, no bytes.
        Produce(
            frames: WarmFrames,
            node: node
        );
        Assert.Same(
            actual: node.Plan,
            expected: installed.Plan
        );
        Assert.Equal(
            actual: (Free: heap.FreeViewDescriptors, Pools: gpu.DescriptorPools.Count, Bytes: gpu.LiveBytes),
            expected: (Free: free, Pools: pools, Bytes: liveBytes)
        );
    }
    [Fact]
    public void ACandidateThatFitsExactlyInstallsAndTheReplacedGraphsRangeServesTheNextCandidate() {
        var installed = Feedback();
        var candidate = Feedback(historyFormat: "R32G32B32A32Float");
        var heap = DeviceHeap(views: (HeapDemand(pipeline: installed) + HeapDemand(pipeline: candidate)));
        var gpu = new FakePipelineGpu {
            DescriptorHeap = heap,
        };
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: installed
        );

        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: candidate
        );
        Assert.Null(@object: node.LastSwapError);
        Assert.Same(
            actual: node.Plan,
            expected: candidate.Plan
        );

        // Once the replaced graph retires its range is free again, and the next candidate is admitted into it.
        Produce(
            frames: WarmFrames,
            node: node
        );
        Assert.Equal(
            actual: (Free: heap.FreeViewDescriptors, Live: heap.LivePools),
            expected: (Free: HeapDemand(pipeline: installed), Live: 1)
        );
        // The queue holds the replaced graph unretired, so the heap holds both graphs at once.
        gpu.QueueHeld = true;
        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback()
        );
        Assert.Null(@object: node.LastSwapError);
        Assert.Equal(
            actual: (Free: heap.FreeViewDescriptors, Live: heap.LivePools),
            expected: (Free: 0U, Live: 2)
        );
        gpu.QueueHeld = false;
    }
}
