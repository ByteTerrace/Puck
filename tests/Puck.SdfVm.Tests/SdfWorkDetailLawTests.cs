using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky's detail rows (<see cref="SdfSkyDetails"/>) and the packed row address agree with the counted sky kernel
/// sites, and the shadow pass counts its slots as kinds of its own row. The field runs own the first rows, each layer label
/// takes the next row the first time it is packed and keeps it, and the rows past the capacity share one.</summary>
public sealed class SdfWorkDetailLawTests {
    [Fact]
    public void SkyAndShadowRowLabelsAndPassBlockReachTheirKernelIndices() {
        var details = new SdfSkyDetails();

        Assert.Equal(expected: new[] { "run0", "run1", "run2" }, actual: details.Labels);
        Assert.Equal(expected: 3u, actual: details.RowOf(label: "gradient"));
        Assert.Equal(expected: 4u, actual: details.RowOf(label: "clouds"));
        Assert.Equal(expected: 3u, actual: details.RowOf(label: "gradient"));
        Assert.Throws<ArgumentException>(testCode: () => details.RowOf(label: "run1"));
        for (var index = 0; (index < SdfSkyDetails.Capacity); index++) {
            _ = details.RowOf(label: $"layer{index}");
        }
        Assert.Equal(expected: SdfSkyDetails.Capacity, actual: details.Labels.Count);
        Assert.Equal(expected: SdfSkyDetails.Overflow, actual: details.Labels[^1]);
        Assert.Equal(expected: ((uint)(SdfSkyDetails.Capacity - 1)), actual: details.RowOf(label: "another"));

        var block = new byte[SdfFrameBlock.SizeBytes];

        SdfFrameBlock.WriteWorkCounterDetailRow(block: block, row: 37);
        var layout = SdfWorldInterfaces.WorldParameters;
        var offset = checked((int)layout.BlockOffsetOf(member: ShaderWorkCounters.DetailRow));

        Assert.Equal(expected: 37u, actual: BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: offset)));
        Assert.DoesNotContain(expectedSubstring: "puckCountDetail", actualString: Source(path: "passes/sdf-world-views.comp.hlsl"));
        Assert.Contains(expectedSubstring: "puckCountShadow(shadowSlot, (sdfWorkSteps - before));", actualString: Source(path: "surface/sdf-shadow.hlsli"));
    }
    // Every kind counts its own evaluation in its layer's row, each hash at the operation that performs it, and the field
    // runs' texture loads in their runs' rows: the base in the lowest run's, the upper images in their runs'.
    [Fact]
    public void SkyHashesAndTextureLoadsCountAtTheOperationsThatPerformThem() {
        foreach (var kind in SdfSkyKindsHlsl.Kinds) {
            Assert.Contains(actualString: Source(path: $"sky/kinds/{kind.Name}.hlsli"), expectedSubstring: "puckCountDetail(layer.Detail, 0u, 0u, 1u, 0u, ");
        }

        var stars = Source(path: "sky/kinds/stars.hlsli");

        Assert.Equal(expected: 3, actual: Regex.Matches(input: stars,
            pattern: @"uint3 h\d? = sdfPcg3d\([^;]+;\s*puckCountDetail\(layer\.Detail, 0u, 0u, 0u, 1u, 0u\);").Count);
        Assert.Matches(actualString: Source(path: "sky/kinds/clouds.hlsli"), expectedRegexPattern: @"sdfPeriodicFbm2\([^;]+;\s*float density = sdfPeriodicFbm2\([^;]+;\s*puckCountDetail\(layer\.Detail, 0u, 0u, 0u, \(8u \* octaves\), 0u\);");
        var runs = Source(path: "passes/sdf-sky-pass.hlsli");

        Assert.Matches(actualString: runs, expectedRegexPattern: @"skyBase.Load\(tap\);\s*puckCountDetail\(0u, 0u, 0u, 0u, 0u, 1u\);");
        Assert.Matches(actualString: runs, expectedRegexPattern: @"skyUpper0.Load\(tap\);\s*float4 upper1 = skyUpper1.Load\(tap\);\s*puckCountDetail\(1u, 0u, 0u, 0u, 0u, 2u\);");
        Assert.Matches(actualString: runs, expectedRegexPattern: @"skyUpper2.Load\(tap\);\s*puckCountDetail\(2u, 0u, 0u, 0u, 0u, 1u\);");
    }

    // Clouds at the low tier hash at most a quarter of what they hash at the high tier a covered pixel: one thickness tap
    // of three octaves (two fractal sums of four lattice corners an octave) against four taps of the kind's octaves, and
    // the low tier returns before any lighting tap.
    [Fact]
    public void CloudsAtTheLowTierHashAQuarterOrLessOfTheirHighTierHashes() {
        var clouds = Source(path: "sky/kinds/clouds.hlsli");
        var low = clouds.IndexOf(value: "if (sample.tier == SDF_SKY_TIER_LOW) {", comparisonType: StringComparison.Ordinal);
        var firstTap = clouds.IndexOf(value: "float thicknessX", comparisonType: StringComparison.Ordinal);

        Assert.InRange(actual: low, high: firstTap, low: 0);
        Assert.Contains(actualString: clouds, expectedSubstring: "uint octaves = ((sample.tier >= SDF_SKY_TIER_HIGH) ? clamp(clouds.Octaves, 1u, 8u) : 3u);");
        Assert.Equal(expected: 4, actual: Regex.Matches(input: clouds, pattern: @"= sdfSkyCloudThickness\(").Count);

        const int HashesAnOctave = (2 * 4);
        var high = ((4 * HashesAnOctave) * ((int)SdfSkyClouds.DefaultOctaves));
        var lowest = ((1 * HashesAnOctave) * 3);

        Assert.True(condition: ((4 * lowest) <= high), userMessage: $"low {lowest} against high {high}");
    }

    private static string Source(string path) => File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: $"{SdfKernelInterfaces.KernelDirectory}/{path}"));
}
