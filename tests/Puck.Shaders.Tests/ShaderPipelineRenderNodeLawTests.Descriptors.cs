namespace Puck.Shaders.Tests;

/// <summary>
/// Descriptor-pool laws for <see cref="ShaderPipelineRenderNode"/>. The node states the pools a plan needs before
/// anything is allocated, and the fake records, independently, the size of every pool the node actually creates. The
/// two agree, pool by pool and in creation order, for the graph alone and with a float preview, so a heap that admits the
/// statement admits what the node requests.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void TheDescriptorPoolsANodeStatesAreThePoolsItCreates(bool floatOutput) {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            floatOutput: floatOutput,
            gpu: gpu
        );

        Assert.Equal(
            expected: ShaderPipelineRenderNode.DescriptorPools(
                inFlight: InFlight,
                plan: node.Plan!,
                preview: floatOutput
            ),
            actual: gpu.DescriptorPools
        );
    }
}
