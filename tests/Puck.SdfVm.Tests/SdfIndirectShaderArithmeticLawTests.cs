using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfIndirectShaderArithmeticLawTests {
    [Fact]
    public void SignedFieldSamplesKeepThePrimaryMarchArithmeticAcrossBackends() {
        // These finite inputs land on opposite sides of the material board's slab when a backend fuses the
        // multiply-add. A sign certificate must use the primary march's separately rounded reconstruction.
        var direction = BitConverter.Int32BitsToSingle(value: -1082131271);
        var distance = BitConverter.Int32BitsToSingle(value: 1050254231);
        var advance = (direction * distance);
        var separate = (20.6f + advance);
        var contracted = MathF.FusedMultiplyAdd(x: direction, y: distance, z: 20.6f);

        Assert.True(condition: ((MathF.Abs(x: (separate - 20.6f)) - 0.3f) > 0));
        Assert.True(condition: ((MathF.Abs(x: (contracted - 20.6f)) - 0.3f) < 0));

        var source = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath:
            "src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-march.hlsli"));

        Assert.Contains(actualString: source, expectedSubstring: "precise float3 advance = direction * distance;");
        Assert.Contains(actualString: source, expectedSubstring: "precise float3 position = origin + advance;");
        // Each marcher's one sample site reads a point every phase reconstructs through sdfIndirectPointAt.
        Assert.Contains(actualString: source, expectedSubstring: "position = sdfIndirectPointAt(origin, direction, result.distance);");
        Assert.Contains(actualString: source, expectedSubstring: "at = sdfIndirectPointAt(position, normal, witnessOffset);");
        Assert.Contains(actualString: source, expectedSubstring: "SdfHit query = sdfIndirectSample(at, queryMask);");
        Assert.Contains(actualString: source, expectedSubstring: "position = sdfIndirectPointAt(a, direction, travel);");
        Assert.Contains(actualString: source, expectedSubstring: "position = sdfIndirectPointAt(a, direction, bracketEnd);");
        Assert.Contains(actualString: source, expectedSubstring: "SdfHit query = sdfIndirectSample(position, SDF_INSTANCE_MASK_ALL);");
        Assert.Contains(actualString: source, expectedSubstring: "blockedPoint = sdfIndirectPointAt(a, direction, lerp(travel, bracketEnd, saturate(weight)));");
    }
}
