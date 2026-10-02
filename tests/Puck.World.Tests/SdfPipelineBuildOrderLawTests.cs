using Puck.SdfVm;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a set leases its pipelines longest bytecode first (<see cref="SdfWorldPipelines.BuildOrder"/>),
/// so with at most <c>GpuPassPipelineCache.BuildConcurrency</c> creations at once the kernel whose cold translation
/// decides when the set is ready takes a turn first instead of waiting behind every other build. Every kernel but the
/// on-demand resolve kernel is leased exactly once; equal lengths keep <see cref="SdfKernel"/> order. Over the build's
/// own SPIR-V and DXIL every kernel starts in descending bytecode length, the longest of them first.
/// </summary>
public sealed class SdfPipelineBuildOrderLawTests {
    [Fact]
    public void TheLongestKernelStartsFirstAndEqualLengthsKeepKernelOrder() {
        // Every kernel one byte long except the sky (longest), the beam and the brick baker (tied), and the resolve kernel,
        // which is longer than all of them and still never leased up front.
        var kernels = new SdfKernelSet(bytecode: [.. SdfKernelSet.Kernels.Select(selector: static kernel => ((ReadOnlyMemory<byte>)new byte[kernel switch {
            SdfKernel.Sky => 9,
            SdfKernel.Beam or SdfKernel.BrickBake => 5,
            SdfKernel.Resolve => 20,
            _ => 1,
        }]))]);

        Assert.Equal(
            actual: SdfWorldPipelines.BuildOrder(kernels: kernels),
            expected: [
                SdfKernel.Sky,
                SdfKernel.Beam,
                SdfKernel.BrickBake,
                SdfKernel.InstanceCull,
                SdfKernel.CullArgs,
                SdfKernel.Primary,
                SdfKernel.Surface,
                SdfKernel.Ambient,
                SdfKernel.Shadow,
                SdfKernel.Views,
                SdfKernel.ViewsCore,
                SdfKernel.ViewsFolds,
                SdfKernel.Composite,
            ]
        );
    }
    [InlineData(".spv")]
    [InlineData(".dxil")]
    [Theory]
    public void TheBuildsOwnKernelsStartLongestFirst(string bytecodeExtension) {
        var kernels = SdfKernelSet.Load(bytecodeExtension: bytecodeExtension);
        var order = SdfWorldPipelines.BuildOrder(kernels: kernels);

        Assert.Equal(
            actual: kernels[order[0]].Length,
            expected: SdfKernelSet.Kernels.Where(predicate: static kernel => (kernel != SdfKernel.Resolve)).Max(selector: kernel => kernels[kernel].Length)
        );
        Assert.Equal(
            actual: order.Select(selector: kernel => kernels[kernel].Length),
            expected: order.Select(selector: kernel => kernels[kernel].Length).OrderDescending()
        );
    }
}
