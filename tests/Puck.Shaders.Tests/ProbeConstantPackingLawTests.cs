namespace Puck.Shaders.Tests;

/// <summary>A kernel-class probe packs its config under HLSL constant-buffer packing
/// (<see cref="ProbeKindManifest.ConstantOffsets"/>). These laws hold the rule to the offsets DXC assigned a fixture
/// block holding one field of every shape the rule places.</summary>
public sealed class ProbeConstantPackingLawTests {
    private const uint DxilSizeBytes = 96;

    // `dxc -Wno-ignored-attributes -enable-16bit-types -O3 -T cs_6_6 -E CSMain -Fc out.txt constant-packing.comp.hlsl`
    // prints these under `cbuffer pc`, one `; Offset:` per member and `Size: 96` for the block.
    private static readonly uint[] DxilOffsets = [0, 4, 12, 16, 28, 32, 36, 48, 64, 68, 80, 88, 92];
    private static readonly ShaderValueType[] FixtureTypes = [
        ShaderValueType.Float,
        ShaderValueType.Float2,
        ShaderValueType.Float,
        ShaderValueType.Float3,
        ShaderValueType.Uint,
        ShaderValueType.Float,
        ShaderValueType.Float2,
        ShaderValueType.Float4,
        ShaderValueType.Int,
        ShaderValueType.Float3,
        ShaderValueType.Uint2,
        ShaderValueType.Uint,
        ShaderValueType.Uint,
    ];

    [Fact]
    public void A_vector_is_bumped_to_the_next_row_only_when_it_would_straddle_one() {
        Assert.Equal(
            expected: [0u, 4u],
            actual: ProbeKindManifest.ConstantOffsets(
                sizeBytes: out var size1,
                types: [ShaderValueType.Float, ShaderValueType.Float2]
            )
        );
        Assert.Equal(
            actual: size1,
            expected: 12u
        );
        Assert.Equal(
            expected: [0u, 4u, 8u],
            actual: ProbeKindManifest.ConstantOffsets(
                sizeBytes: out _,
                types: [ShaderValueType.Float, ShaderValueType.Float, ShaderValueType.Float2]
            )
        );
        Assert.Equal(
            expected: [0u, 4u, 8u, 16u],
            actual: ProbeKindManifest.ConstantOffsets(
                sizeBytes: out _,
                types: [ShaderValueType.Float, ShaderValueType.Float, ShaderValueType.Float, ShaderValueType.Float2]
            )
        );
        Assert.Equal(
            expected: [0u, 4u],
            actual: ProbeKindManifest.ConstantOffsets(
                sizeBytes: out _,
                types: [ShaderValueType.Float, ShaderValueType.Float3]
            )
        );
        Assert.Equal(
            expected: [0u, 4u, 16u],
            actual: ProbeKindManifest.ConstantOffsets(
                sizeBytes: out _,
                types: [ShaderValueType.Float, ShaderValueType.Float, ShaderValueType.Float3]
            )
        );
        Assert.Equal(
            expected: [0u, 16u, 32u],
            actual: ProbeKindManifest.ConstantOffsets(
                sizeBytes: out var size2,
                types: [ShaderValueType.Float, ShaderValueType.Float4, ShaderValueType.Uint]
            )
        );
        Assert.Equal(
            actual: size2,
            expected: 36u
        );
    }
    [Fact]
    public void The_rule_matches_the_offsets_dxc_assigned_in_the_compiled_spirv() {
        var spirvOffsets = SpirvStructOffsets.OfNamed(
            module: File.ReadAllBytes(path: Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: "Assets",
                path3: "Shaders",
                path4: "constant-packing.comp.spv"
            )),
            structName: "type.ConstantBuffer.LayoutPushData"
        );

        Assert.Equal(
            actual: ProbeKindManifest.ConstantOffsets(
                sizeBytes: out _,
                types: FixtureTypes
            ),
            expected: spirvOffsets
        );
    }
    [Fact]
    public void The_rule_matches_the_offsets_dxc_assigned_in_the_dxil() {
        Assert.Equal(
            actual: ProbeKindManifest.ConstantOffsets(
                sizeBytes: out var sizeBytes,
                types: FixtureTypes
            ),
            expected: DxilOffsets
        );
        Assert.Equal(
            actual: sizeBytes,
            expected: DxilSizeBytes
        );
    }
}
