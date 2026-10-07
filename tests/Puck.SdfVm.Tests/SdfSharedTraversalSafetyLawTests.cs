using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfSharedTraversalSafetyLawTests {
    [Fact]
    public void LightCameraConeSearchHasAStaticOuterBoundAcrossItsPhases() {
        var source = Source(path: "march/sdf-cone.hlsli");

        Assert.Contains(actualString: source, expectedSubstring: "iteration <= (uint)(max(ConeMarchSteps, IndependentConeMarchSteps) + TileGapSteps + TileFarSteps)");
        Assert.DoesNotContain(actualString: source, expectedSubstring: "while (true)");
    }
    [Fact]
    public void GridTraversalChecksRealStorageAndCapsEmptyCellAdvancement() {
        var program = Source(path: "field/sdf-program.hlsli");
        var walk = Source(path: "march/sdf-grid-walk.hlsli");

        Assert.Contains(actualString: program, expectedSubstring: "sdfFrameInstanceGrid.GetDimensions(wordCount, stride);");
        Assert.Contains(actualString: program, expectedSubstring: "wordCount < SDF_GRID_HEADER_WORDS");
        Assert.Contains(actualString: program, expectedSubstring: "grid.alwaysCount <= wordCount - grid.alwaysWord");
        Assert.Contains(actualString: program, expectedSubstring: "grid.entryCount <= instanceCount - min(grid.alwaysCount, instanceCount)");
        Assert.Contains(actualString: program, expectedSubstring: "relativeWord >= grid.wordCount");
        Assert.Contains(actualString: walk, expectedSubstring: "advance < SDF_GRID_MAX_ADVANCES");
        Assert.Contains(actualString: walk, expectedSubstring: "walk.end > grid.entryCount");
        Assert.Contains(actualString: walk, expectedSubstring: "return instance < grid.instanceCount;");
        Assert.Contains(actualString: walk, expectedSubstring: "walk.slab >= SDF_GRID_MAX_SLABS");
        Assert.DoesNotContain(actualString: walk, expectedSubstring: "while (true)");
    }
    [Fact]
    public void GridCoordinatesAreFiniteAndClippedBeforeIntegerConversion() {
        var source = Source(path: "march/sdf-grid-walk.hlsli");

        Assert.Contains(actualString: source, expectedSubstring: "float3 lowerCell = clamp(floor(");
        Assert.Contains(actualString: source, expectedSubstring: "float3 upperCell = clamp(floor(");
        Assert.Contains(actualString: source, expectedSubstring: "!all(isfinite(lowerCell)) || !all(isfinite(upperCell))");
        Assert.Contains(actualString: source, expectedSubstring: "walk.low = int3(lowerCell);");
        Assert.Contains(actualString: source, expectedSubstring: "int3 upper = int3(upperCell);");
        Assert.DoesNotContain(actualString: source, expectedSubstring: "clamp(int3(floor(");
    }
    [Fact]
    public void SharedLightReadersRespectTableCapacityAndActualBufferLengths() {
        var frame = Source(path: "frame/sdf-lights.hlsli");
        var shade = Source(path: "shade/sdf-light.hlsli");

        Assert.Contains(actualString: frame, expectedSubstring: "sdfLights.GetDimensions(count, stride);");
        Assert.Contains(actualString: frame, expectedSubstring: "min(passGroup.lightCount, min(count, SDF_MAX_LIGHTS))");
        Assert.Contains(actualString: frame, expectedSubstring: "sdfDynamicTransforms.GetDimensions(count, stride);");
        Assert.Contains(actualString: frame, expectedSubstring: "(uint)light.DynamicSlot >= count / 3u");
        Assert.Contains(actualString: frame, expectedSubstring: "sdfShadowHandoffs.GetDimensions(count, stride);");
        Assert.Contains(actualString: shade, expectedSubstring: "(uint)index >= worldLightCount()");
        Assert.Contains(actualString: shade, expectedSubstring: "shadowSlot < SDF_MAX_SHADOW_SLOTS");
        Assert.Contains(actualString: shade, expectedSubstring: "handoff.Slot < 0 || (uint)handoff.Slot >= SDF_MAX_SHADOW_SLOTS");
        Assert.Contains(actualString: shade, expectedSubstring: "screenIndex >= surfaceRows / WorldScreenSurfaceRows");
        Assert.Contains(actualString: shade, expectedSubstring: "screenIndex >= mappingRows / WorldScreenMappingRows");
        Assert.Contains(actualString: shade, expectedSubstring: "screenIndex >= emissionRows / SDF_SCREEN_EMISSION_RECORDS");
    }
    [Fact]
    public void BothInterpretersBoundDirectoriesProgressAndFieldStacks() {
        var layout = Source(path: "field/sdf-layout.hlsli");
        var program = Source(path: "field/sdf-program.hlsli");

        Assert.Contains(actualString: program, expectedSubstring: "count <= (capacity - first) / stride");
        Assert.Contains(actualString: program, expectedSubstring: "if (index >= sdfProgramVectorCount())");
        Assert.Contains(actualString: layout, expectedSubstring: "sdfProgramRange(SDF_PROGRAM_DATA_OFFSET(header), instructions, SDF_INSTRUCTION_DATA_VECTORS)");
        Assert.Contains(actualString: layout, expectedSubstring: "sdfProgramRange(boundsOffset, instructions, SDF_BOUND_RECORD_VECTORS)");
        Assert.Contains(actualString: layout, expectedSubstring: "candidate < SDF_MAX_INSTANCES");
        foreach (var name in new[] { "field/sdf-map.hlsli", "field/sdf-map-grad.hlsli" }) {
            var source = Source(path: name);

            Assert.Contains(actualString: source, expectedSubstring: "!sdfProgramLayout.valid");
            Assert.Contains(actualString: source, expectedSubstring: "merge < sdfProgramLayout.vectorCount");
            Assert.Contains(actualString: source, expectedSubstring: "segment <= previousSegment");
            Assert.Contains(actualString: source, expectedSubstring: "sdfProgramRange(plan.x, plan.y, 3u)");
            Assert.Contains(actualString: source, expectedSubstring: "min(segmentMeta.w, sdfProgramLayout.instructionCount)");
            Assert.Contains(actualString: source, expectedSubstring: "fieldDepth >= SDF_MAX_FIELD_SCOPE_DEPTH");
            Assert.Contains(actualString: source, expectedSubstring: "fieldDepth == 0u");
            Assert.DoesNotContain(actualString: source, expectedSubstring: "for (;;)");
        }
    }
    [Fact]
    public void ShapeSideTablesAndFloatCountsStayWithinAuthoredLimits() {
        var shapes = Source(path: "field/sdf-shapes.hlsli");
        var noise = Source(path: "field/sdf-noise.hlsli");

        Assert.Contains(actualString: shapes, expectedSubstring: "count > SDF_MAX_CONVEX_VERTICES");
        Assert.Contains(actualString: shapes, expectedSubstring: "!isfinite(data0.y)");
        Assert.Contains(actualString: shapes, expectedSubstring: "sdfProgramRange(offset, count, 2u)");
        Assert.Contains(actualString: shapes, expectedSubstring: "i < SDF_MAX_PATH_EDGES");
        Assert.Contains(actualString: shapes, expectedSubstring: "!isfinite(strandsFloat)");
        Assert.Contains(actualString: shapes, expectedSubstring: "strand < SDF_MAX_SWEEP_STRANDS");
        Assert.Contains(actualString: noise, expectedSubstring: "octave < SDF_MAX_NOISE_OCTAVES");
        Assert.Contains(actualString: shapes, expectedSubstring: "voxelCount > numVoxels - baseWord");
        Assert.Contains(actualString: shapes, expectedSubstring: "if (!all(isfinite(local)))");
    }
    [Fact]
    public void DecodedDomainAxesAreRejectedBeforeVectorIndexing() {
        foreach (var name in new[] { "field/sdf-map.hlsli", "field/sdf-map-grad.hlsli" }) {
            var source = Source(path: name);

            Assert.Contains(actualString: source, expectedSubstring: "if (driver >= 3u) { return sdfIsaErrorHit(); }\n                    float angle");
            Assert.Contains(actualString: source, expectedSubstring: "if (axis >= 3u) { return sdfIsaErrorHit(); }\n                    float rawT");
            Assert.Contains(actualString: source, expectedSubstring: "if (target >= 3u || driver >= 3u) { return sdfIsaErrorHit(); }\n                    float t");
        }
        var parts = Source(path: "field/sdf-parts.hlsli");

        Assert.Contains(actualString: parts, expectedSubstring: "if (axis >= 3u) { parent = sdfIsaErrorHit(); return; }\n                float rawT");
        Assert.Contains(actualString: parts, expectedSubstring: "if (target >= 3u || driver >= 3u) { parent = sdfIsaErrorHit(); return; }\n                float t");
    }

    private static string Source(string path) => File.ReadAllText(path: RepositoryPaths.Resolve(relativePath:
        $"src/Puck.SdfVm/Assets/Shaders/Sdf/{path}"));
}
