using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a mesh raster pass built from the deployed stages keys every pipeline it leases by the bytecode
/// of the stage files beside the build: the mesh, shadow and glass raster pipelines and the occlusion kernel.
/// </summary>
public sealed class SdfMeshRasterPassLawTests {
    private static ReadOnlyMemory<byte> Stage(string stem, string stage) =>
        File.ReadAllBytes(path: Path.Combine(
            path1: SdfKernelSet.DefaultDirectory,
            path2: $"{stem}.{stage}.spv"
        ));

    [Fact]
    public void TheDeployedStagesKeyEveryPipelineThePassLeases() {
        var pass = new SdfMeshRasterPass(
            bytecodeExtension: ".spv",
            pipelines: new GpuPassPipelineCache()
        );
        var vertex = Stage(stage: "vert", stem: SdfMeshRasterPass.ShaderStem);

        Assert.Equal(
            expected: SdfMeshRasterPass.KeyOf(
                fragment: Stage(stage: "frag", stem: SdfMeshRasterPass.ShaderStem),
                vertex: vertex
            ),
            actual: pass.Key
        );
        Assert.Equal(
            expected: SdfMeshRasterPass.ShadowKeyOf(
                fragment: Stage(stage: "frag", stem: SdfMeshRasterPass.ShadowShaderStem),
                vertex: Stage(stage: "vert", stem: SdfMeshRasterPass.ShadowShaderStem)
            ),
            actual: pass.ShadowKey
        );
        Assert.Equal(
            expected: SdfMeshRasterPass.GlassKeyOf(
                fragment: Stage(stage: "frag", stem: SdfMeshRasterPass.GlassShaderStem),
                vertex: vertex
            ),
            actual: pass.GlassKey
        );
        Assert.Equal(
            expected: SdfMeshRasterPass.OcclusionKeyOf(occlusion: Stage(stage: "comp", stem: SdfMeshRasterPass.OcclusionShaderStem)),
            actual: pass.OcclusionKey
        );
    }
}
