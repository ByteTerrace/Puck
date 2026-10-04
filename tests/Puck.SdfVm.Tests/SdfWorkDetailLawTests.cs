using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky's detail rows (<see cref="SdfSkyDetails"/>) and the packed row address agree with the counted sky kernel
/// sites, and the shadow pass counts its slots as kinds of its own row. The field runs own the first rows and the
/// composite's atmosphere, indirect diagnostics and incoming Near work the next. Each layer label takes the next row the first time it is
/// packed and keeps it throughout the composition's lifetime.</summary>
public sealed class SdfWorkDetailLawTests {
    [Fact]
    public void SkyAndShadowRowLabelsAndPassBlockReachTheirKernelIndices() {
        var details = new SdfSkyDetails();

        // The runs' rows, then the composite's atmosphere row, which the composite counts each atmosphere kind it evaluates
        // in (SDF_SKY_DETAIL_ATMOSPHERE), the view's indirect diagnostics, then the layers' labels.
        Assert.Equal(expected: new[] { "run0", "run1", "run2", "atmosphere", "indirect", "indirect-near" }, actual: details.Labels);
        Assert.Equal(actual: 3u, expected: ((uint)SdfSkyDetails.AtmosphereRow));
        Assert.Contains(expectedSubstring: "sdfCountSky(SDF_SKY_DETAIL_ATMOSPHERE, ", actualString: Source(path: "passes/sdf-composite.comp.hlsl"));
        Assert.Contains(expectedSubstring: "#define SDF_SKY_DETAIL_ATMOSPHERE 3u", actualString: Source(path: "isa/sdf-sky-kinds.hlsli"));
        Assert.Equal(expected: 6u, actual: details.RowOf(label: "gradient"));
        Assert.Equal(expected: 7u, actual: details.RowOf(label: "clouds"));
        Assert.Equal(expected: 6u, actual: details.RowOf(label: "gradient"));
        Assert.Throws<ArgumentException>(testCode: () => details.RowOf(label: "run1"));
        Assert.Throws<ArgumentException>(testCode: () => details.RowOf(label: SdfSkyDetails.Atmosphere));
        Assert.Throws<ArgumentException>(testCode: () => details.RowOf(label: SdfSkyDetails.Indirect));
        Assert.True(condition: (SdfSkyDetails.IsFixed(label: "run2") && SdfSkyDetails.IsFixed(label: "atmosphere")
            && SdfSkyDetails.IsFixed(label: "indirect") && !SdfSkyDetails.IsFixed(label: "gradient")));
        Assert.Throws<ArgumentException>(testCode: () => details.RowOf(label: SdfSkyDetails.IndirectNear));
        Assert.True(condition: SdfSkyDetails.IsFixed(label: "indirect-near"));
        Assert.Contains(expectedSubstring: "#define SDF_SKY_DETAIL_INDIRECT_NEAR 5u", actualString: SdfSkyKindsHlsl.Generate());
        var retained = details.Labels;
        for (var index = 0; (index < SdfSkyDetails.InitialCapacity); index++) {
            Assert.Equal(expected: (uint)(8 + index), actual: details.RowOf(label: $"layer{index}"));
        }
        Assert.Equal(expected: 8 + SdfSkyDetails.InitialCapacity, actual: details.Labels.Count);
        Assert.Equal(expected: 8, actual: retained.Count);
        Assert.Equal(expected: retained, actual: details.Labels.Take(retained.Count));
        Assert.Equal(expected: 6u, actual: details.RowOf(label: "gradient"));
        Assert.Equal(expected: (uint)details.Labels.Count, actual: details.RowOf(label: "another"));
        Assert.Equal(expected: "another", actual: details.Labels[^1]);

        var block = new byte[SdfFrameBlock.SizeBytes];

        SdfFrameBlock.WriteWorkCounterDetailRow(block: block, row: 37);
        var layout = SdfWorldInterfaces.WorldParameters;
        var offset = checked((int)layout.BlockOffsetOf(member: ShaderWorkCounters.DetailRow));

        Assert.Equal(expected: 37u, actual: BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: offset)));
        Assert.DoesNotContain(expectedSubstring: "sdfCountSky", actualString: Source(path: "passes/sdf-world-views.comp.hlsl"));
        Assert.DoesNotContain(expectedSubstring: "puckCountDetail", actualString: Source(path: "passes/sdf-world-views.comp.hlsl"));
        Assert.Contains(expectedSubstring: "puckCountShadow(shadowSlot, (sdfWorkSteps - before));", actualString: Source(path: "surface/sdf-shadow.hlsli"));
    }
    [Fact]
    public void IndirectDiagnosticsKeepAReservedRowAsSkyLabelsGrow() {
        var details = new SdfSkyDetails();
        var before = details.Labels.ToArray();

        Assert.Equal(actual: SdfSkyDetails.IndirectRow, expected: 4);
        Assert.Equal(expected: "indirect", actual: before[SdfSkyDetails.IndirectRow]);
        for (var index = 0; (index < (SdfSkyDetails.InitialCapacity + 1)); index++) {
            _ = details.RowOf(label: $"layer{index}");
        }
        Assert.Equal(expected: before, actual: details.Labels.Take(count: before.Length));
        Assert.Contains(expectedSubstring: "#define SDF_SKY_DETAIL_INDIRECT 4u", actualString: SdfSkyKindsHlsl.Generate());
        var reads = Source(path: "indirect/sdf-indirect-read.hlsli");

        Assert.Equal(expected: 2, actual: Regex.Matches(input: reads, pattern: @"puckCountIndirect\(SDF_SKY_DETAIL_INDIRECT, 0u,").Count);
        Assert.DoesNotContain(actualString: reads, expectedSubstring: "puckCountIndirect(0u,");
    }
    // Every kind counts its own evaluation in its layer's row, each hash at the operation that performs it, and the field
    // runs' texture loads in their runs' rows: the base in the lowest run's, the upper images in their runs'.
    [Fact]
    public void SkyHashesAndTextureLoadsCountAtTheOperationsThatPerformThem() {
        var wrapper = Source(path: "sky/sdf-sky.hlsli");

        Assert.Matches(actualString: wrapper, expectedRegexPattern: @"void sdfCountSky\([^)]*\)\s*\{\s*sdfSkyCost \+= float3\(evaluations, hashes, loads\);\s*puckCountDetail\(detail, steps, texels, evaluations, hashes, loads\);\s*\}");
        Assert.Single(collection: Regex.Matches(input: wrapper, pattern: @"\bpuckCountDetail\("));
        foreach (var kind in SdfSkyKindsHlsl.Kinds) {
            Assert.Contains(actualString: Source(path: $"sky/kinds/{kind.Name}.hlsli"), expectedSubstring: "sdfCountSky(layer.Detail, 0u, 0u, 1u, 0u, ");
        }

        var stars = Source(path: "sky/kinds/stars.hlsli");

        Assert.Equal(expected: 3, actual: Regex.Matches(input: stars,
            pattern: @"uint3 h\d? = sdfPcg3d\([^;]+;\s*sdfCountSky\(layer\.Detail, 0u, 0u, 0u, 1u, 0u\);").Count);
        Assert.Matches(actualString: Source(path: "sky/kinds/clouds.hlsli"), expectedRegexPattern: @"sdfPeriodicFbm2\([^;]+;\s*float density = sdfPeriodicFbm2\([^;]+;\s*sdfCountSky\(layer\.Detail, 0u, 0u, 0u, \(8u \* octaves\), 0u\);");
        var runs = Source(path: "passes/sdf-sky-pass.hlsli");

        Assert.Matches(actualString: runs, expectedRegexPattern: @"skyBase.Load\(tap\);\s*sdfCountSky\(0u, 0u, 0u, 0u, 0u, 1u\);");
        Assert.Matches(actualString: runs, expectedRegexPattern: @"skyUpper0.Load\(tap\);\s*float4 upper1 = skyUpper1.Load\(tap\);\s*sdfCountSky\(1u, 0u, 0u, 0u, 0u, 2u\);");
        Assert.Matches(actualString: runs, expectedRegexPattern: @"skyUpper2.Load\(tap\);\s*sdfCountSky\(2u, 0u, 0u, 0u, 0u, 1u\);");
    }
    // Clouds at the low tier hash at most a quarter of what they hash at the high tier a covered pixel: one thickness tap
    // of three octaves (two fractal sums of four lattice corners an octave) against four taps of the kind's octaves, and
    // the low tier returns before any lighting tap.
    [Fact]
    public void CloudsAtTheLowTierHashAQuarterOrLessOfTheirHighTierHashes() {
        var clouds = Source(path: "sky/kinds/clouds.hlsli");
        var low = clouds.IndexOf(comparisonType: StringComparison.Ordinal, value: "if (sample.tier == SDF_SKY_TIER_LOW) {");
        var firstTap = clouds.IndexOf(comparisonType: StringComparison.Ordinal, value: "float thicknessX");

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
