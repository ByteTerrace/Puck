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
        Assert.Contains(actualString: source, expectedSubstring: "float3 position = sdfIndirectPointAt(origin, direction, result.distance);");
        Assert.Contains(actualString: source, expectedSubstring: "sdfIndirectSample(sdfIndirectPointAt(position, normal, witnessOffset), SDF_INSTANCE_MASK_ALL)");
        Assert.Contains(actualString: source, expectedSubstring: "float3 position = sdfIndirectPointAt(a, direction, travel);");
        Assert.Contains(actualString: source, expectedSubstring: "sdfIndirectSample(sdfIndirectPointAt(a, direction, bracketEnd), SDF_INSTANCE_MASK_ALL)");
        Assert.Contains(actualString: source, expectedSubstring: "blockedPoint = sdfIndirectPointAt(a, direction, lerp(travel, bracketEnd, saturate(weight)));");
    }
}
