using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Indirect readers refuse malformed coordinates and bound resource-derived work before indexing.
/// These CPU checks pin the shader guards; the device parity leg owns execution on each backend.</summary>
public sealed class SdfIndirectShaderInputLawTests {
    [Fact]
    public void LightMapsUseTheirAllocatedDepthLengthAndTheFixedMetadataCapacity() {
        var lookup = Source(file: "indirect/sdf-indirect-light.hlsli");
        var writer = Source(file: "passes/sdf-light-depth.comp.hlsl");

        Assert.Contains(actualString: lookup, expectedSubstring: "indirectLightDepth.GetDimensions(depthCount, depthStride)");
        Assert.Contains(actualString: lookup, expectedSubstring: "min(min(passGroup.lightMapCount, SdfIndirectLightMaxMaps)");
        Assert.Contains(actualString: lookup, expectedSubstring: "depthCount / (SdfIndirectLightResolution * SdfIndirectLightResolution)");
        Assert.Contains(actualString: lookup, expectedSubstring: "index < mapCount");
        Assert.Contains(actualString: writer, expectedSubstring: "uint3 slice = sdfIndirectLightSlice(passGroup.lightSlice);");
        Assert.Contains(actualString: writer, expectedSubstring: "if (slice.x == 0u || id.y >= slice.z * SdfIndirectLightSliceRowEdge) { return; }");
        Assert.Contains(actualString: writer, expectedSubstring: "indirectLightDepthRW.GetDimensions(depthCount, depthStride)");
        Assert.Contains(actualString: writer, expectedSubstring: "if (address >= depthCount) { return; }");
    }
    [Fact]
    public void FiniteProjectionStillClampsTheRoundedUpperEdgeBeforeIndexing() {
        // A finite plane coordinate strictly below one rounds onto the first pixel outside the map.
        var edge = MathF.BitDecrement(x: 1f);
        var pixel = ((uint)((edge + 1f) * (0.5f * SdfIndirectLightLayout.Resolution)));

        Assert.True(condition: (edge < 1f));
        Assert.Equal(actual: pixel, expected: ((uint)SdfIndirectLightLayout.Resolution));
        var source = Source(file: "indirect/sdf-indirect-light-projection.hlsli");

        Assert.Contains(actualString: source, expectedSubstring: "any(!isfinite(plane))");
        Assert.Contains(actualString: source, expectedSubstring: "!isfinite(right.w) || right.w <= 0.0");
        Assert.Contains(actualString: source, expectedSubstring: "pixel = min((uint2)");
        Assert.Contains(actualString: source, expectedSubstring: "resolution - 1u)");
    }
    [Fact]
    public void StoredLightingValidatesGenerationLatticeAndScreenBufferExtents() {
        var irradiance = Source(file: "indirect/sdf-indirect-irradiance.hlsli");
        var continuation = Source(file: "indirect/sdf-indirect-continuation.hlsli");
        var screen = Source(file: "indirect/sdf-indirect-screen.hlsli");

        Assert.Contains(actualString: irradiance, expectedSubstring: "generation >= SdfIndirectLightingGenerations");
        Assert.Contains(actualString: irradiance, expectedSubstring: "any(!isfinite(normal))");
        Assert.Contains(actualString: irradiance, expectedSubstring: "sdfIndirectCellAt(launched");
        Assert.Contains(actualString: continuation, expectedSubstring: "generation >= SdfIndirectLightingGenerations");
        Assert.Contains(actualString: continuation, expectedSubstring: "sdfIndirectCellAt(position");
        Assert.Contains(actualString: screen, expectedSubstring: "screenMappings.GetDimensions(mappingCount, stride)");
        Assert.Contains(actualString: screen, expectedSubstring: "screenSurfaces.GetDimensions(surfaceCount, stride)");
        Assert.Contains(actualString: screen, expectedSubstring: "sdfScreenLights.GetDimensions(emissionCount, stride)");
        Assert.Contains(actualString: screen, expectedSubstring: "screen >= emissionCount / SDF_SCREEN_EMISSION_RECORDS");
        Assert.Contains(actualString: screen, expectedSubstring: "any(!isfinite(at))");
    }
    [Fact]
    public void ReceiverRecordsAndShadowSlotsHaveFiniteResourceBounds() {
        var certificate = Source(file: "indirect/sdf-indirect-receiver-certificate.hlsli");
        var receiver = Source(file: "indirect/sdf-indirect-apply.hlsli");
        var diffuse = Source(file: "indirect/sdf-indirect-diffuse.hlsli");

        Assert.Contains(actualString: certificate, expectedSubstring: "sdfVisibilityRecordBuffer.GetDimensions(count, stride)");
        Assert.Contains(actualString: certificate, expectedSubstring: "record <= count && SdfVisibilityWords <= count - record");
        Assert.Contains(actualString: receiver, expectedSubstring: "!sdfIndirectReceiverRecordValid(record)");
        Assert.Contains(actualString: receiver, expectedSubstring: "source >= meshWords / SdfMeshDrawWords");
        Assert.Contains(actualString: receiver, expectedSubstring: "!sdfProgramLayout.valid || source > sdfProgramLayout.instanceCount");
        Assert.Contains(actualString: receiver, expectedSubstring: "sdfInstanceEntryOffset(sdfProgramLayout.instanceOffset, source - 1u)");
        Assert.Contains(actualString: diffuse, expectedSubstring: "min(passGroup.shadowSlotCount, SDF_MAX_SHADOW_SLOTS)");
        Assert.Contains(actualString: diffuse, expectedSubstring: "sdfShadowHandoffs.GetDimensions(handoffCount, handoffStride)");
        Assert.Contains(actualString: diffuse, expectedSubstring: "SDF_SHADOW_FADE_SLOTS), handoffCount)");
    }
    [Fact]
    public void MaterialRowsValidateTheWholeTableBeforeReadingItsPayload() {
        var source = Source(file: "shade/sdf-material.hlsli");

        Assert.Contains(actualString: source, expectedSubstring: "uint4 header = sdfProgramWord(0);");
        Assert.Contains(actualString: source, expectedSubstring: "!sdfProgramRange(SDF_PROGRAM_MATERIAL_OFFSET(header), SDF_PROGRAM_MATERIAL_COUNT(header), SDF_MATERIAL_VECTORS_PER_ENTRY)");
        Assert.True(condition: (source.IndexOf(comparisonType: StringComparison.Ordinal, value: "!sdfProgramRange") < source.IndexOf(comparisonType: StringComparison.Ordinal, value: "uint materialBase")));
    }
    [Fact]
    public void CullArgumentsCannotOverflowTheirStrideOrKeepStaleExecutableCounts() {
        var source = Source(file: "passes/sdf-cull-args.comp.hlsl");

        Assert.Contains(actualString: source, expectedSubstring: "tileGrid <= SDF_MAX_DISPATCH_GROUPS / groupsPerTile");
        Assert.Contains(actualString: source, expectedSubstring: "all(passGroup.tileGrid == tileGrid)");
        Assert.Contains(actualString: source, expectedSubstring: "valid = v < tileCount / total");
        Assert.Contains(actualString: source, expectedSubstring: "passGroup.lightSlice == 0u || slice.x != 0u");
        Assert.Contains(actualString: source, expectedSubstring: "if (boundsCount < 4u)");
        Assert.Contains(actualString: source, expectedSubstring: "viewsArgsRW[0] = 0u; viewsArgsRW[1] = 0u; viewsArgsRW[2] = 0u");
        Assert.Contains(actualString: source, expectedSubstring: "tiles[v * total + entry]");
        // The validated product cannot wrap at the strided loop's last increment, even for hostile frame values.
        var tilesPerAxis = (GpuRegion.CopyMaxGroupsPerDimension / 2UL);

        Assert.True(condition: (((tilesPerAxis * tilesPerAxis) + 256UL) < uint.MaxValue));
        var generator = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "src/Puck.SdfVm.Model/SdfIsaHlsl.cs"));

        Assert.Contains(actualString: generator, expectedSubstring: "name: \"SDF_MAX_DISPATCH_GROUPS\", value: checked((int)GpuRegion.CopyMaxGroupsPerDimension)");
    }
    [Fact]
    public void ConeTransportKeepsAnIndependentStaticTripBound() {
        var source = Source(file: "indirect/sdf-indirect-alternatives.hlsli");

        Assert.Contains(actualString: source, expectedSubstring: "step < SdfIndirectAlternativeConeSteps && budget > 0u");
        Assert.DoesNotContain(actualString: source, expectedSubstring: "while (budget > 0u)");
    }

    private static string Source(string file) => File.ReadAllText(path: RepositoryPaths.Resolve(relativePath:
        ("src/Puck.SdfVm/Assets/Shaders/Sdf/" + file)));
}
