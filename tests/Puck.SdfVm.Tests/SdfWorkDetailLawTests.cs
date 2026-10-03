using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The CPU row labels and packed row address agree with the counted sky kernel sites, and the shadow pass counts its slots as kinds of its own row.</summary>
public sealed class SdfWorkDetailLawTests {
    [Fact]
    public void IndirectDetailIndicesNameEachLevelLaunchAndProof() {
        Assert.Equal(new[] { "near", "room", "world", "launch", "proof" }, SdfWorldWorkDetails.Indirect);
        Assert.Equal(new[] { "indirect" }, SdfWorldWorkDetails.Of(SdfWorldPackage.Parts.Views));
    }
    [Fact]
    public void SkyAndShadowRowLabelsAndPassBlockReachTheirKernelIndices() {
        Assert.Equal(expected: new[] { "gradient", "disc", "stars", "clouds" }, actual: SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Sky));
        Assert.Equal(expected: SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Sky), actual: SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Composite));
        Assert.Empty(collection: SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Shadow));
        var block = new byte[SdfFrameBlock.SizeBytes];

        SdfFrameBlock.WriteWorkCounterDetailRow(block: block, row: 37);
        var layout = SdfWorldInterfaces.WorldParameters;
        var offset = checked((int)layout.BlockOffsetOf(member: ShaderWorkCounters.DetailRow));

        Assert.Equal(expected: 37u, actual: BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: offset)));
        Assert.DoesNotContain(expectedSubstring: "puckCountDetail", actualString: Source(path: "passes/sdf-world-views.comp.hlsl"));
        Assert.Contains(expectedSubstring: "puckCountShadow(shadowSlot, (sdfWorkSteps - before));", actualString: Source(path: "surface/sdf-shadow.hlsli"));
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
