using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The CPU row labels and packed row address agree with the counted sky and shadow kernel sites.</summary>
public sealed class SdfWorkDetailLawTests {
    [Fact]
    public void SkyAndShadowRowLabelsAndPassBlockReachTheirKernelIndices() {
        Assert.Equal(expected: new[] { "gradient", "disc", "stars", "clouds" }, actual: SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Sky));
        Assert.Equal(expected: SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Sky), actual: SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Composite));
        Assert.Equal(expected: new[] { "slot:0" }, actual: SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Shadow));
        var block = new byte[SdfFrameBlock.SizeBytes];

        SdfFrameBlock.WriteWorkCounterDetailRow(block: block, row: 37);
        var layout = SdfWorldInterfaces.WorldParameters;
        var offset = checked((int)layout.BlockOffsetOf(member: ShaderWorkCounters.DetailRow));

        Assert.Equal(expected: 37u, actual: BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: offset)));
        Assert.Contains(expectedSubstring: "puckCountDetail(0u, sdfWorkSteps, sdfWorkTexels, 0u, 0u, 0u);", actualString: Source(path: "passes/sdf-world-views.comp.hlsl"));
    }
    [Fact]
    public void SkyHashesAndTextureLoadsCountAtTheOperationsThatPerformThem() {
        var sky = Source(path: "shade/sdf-sky.hlsli");

        Assert.Equal(expected: 3, actual: Regex.Matches(input: sky,
            pattern: @"uint3 h\d? = sdfPcg3d\([^;]+;\s*puckCountDetail\(2u, 0u, 0u, 0u, 1u, 0u\);").Count);
        Assert.Matches(actualString: sky, expectedRegexPattern: @"sdfPeriodicNoise2\([^;]+;\s*puckCountDetail\(3u, 0u, 0u, 0u, 4u, 0u\);");
        Assert.Contains(actualString: sky, expectedSubstring: "puckCountDetail(0u, 0u, 0u, 1u, 0u, 0u);");
        Assert.Contains(actualString: sky, expectedSubstring: "puckCountDetail(1u, 0u, 0u, 1u, 0u, 0u);");
        Assert.Contains(actualString: sky, expectedSubstring: "puckCountDetail(2u, 0u, 0u, 1u, 0u, 0u);");
        Assert.Contains(actualString: sky, expectedSubstring: "puckCountDetail(3u, 0u, 0u, 1u, 0u, 0u);");
        var runs = Source(path: "passes/sdf-sky-pass.hlsli");

        Assert.Matches(actualString: runs, expectedRegexPattern: @"skyBase.Load\(tap\);\s*puckCountDetail\(0u, 0u, 0u, 0u, 0u, 1u\);");
        Assert.Matches(actualString: runs, expectedRegexPattern: @"skyOffset.Load\(tap\).rgb\);\s*puckCountDetail\(3u, 0u, 0u, 0u, 0u, 2u\);");
    }

    private static string Source(string path) => File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: $"{SdfKernelInterfaces.KernelDirectory}/{path}"));
}
