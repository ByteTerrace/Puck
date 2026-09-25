using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>The pass interface model with no compiler: the layout rule, the strict document, the hash, and the
/// generated text.</summary>
public sealed class ShaderInterfaceLawTests {
    private static ShaderInterfaceBlockMember Member(string name, uint offset, ShaderValueType type, uint length = 0) =>
        new(
            Length: length,
            Name: name,
            Offset: offset,
            Type: type
        );
    private static ShaderInterfaceBlockMember Pad(uint offset) =>
        Member(
            name: $"_pad{offset}",
            offset: offset,
            type: ShaderValueType.Uint
        );

    [Fact]
    public void The_layout_places_groups_bindings_and_padded_offsets_by_the_rule() {
        Assert.Equal(
            actual: ShaderInterfaceSpike.FilmGrain.Layout().Bindings,
            expected: [
                new ShaderInterfaceBinding(
                    Binding: 0,
                    Kind: GpuBindingKind.ConstantBuffer,
                    Members: [
                        Member(name: "tick", offset: 0, type: ShaderValueType.Uint),
                        Pad(offset: 4),
                        Member(name: "extent", offset: 8, type: ShaderValueType.Uint2),
                    ],
                    Name: "frameGroup",
                    Set: 0
                ),
                new ShaderInterfaceBinding(
                    Binding: 0,
                    Kind: GpuBindingKind.ConstantBuffer,
                    Members: [
                        Member(name: "intensity", offset: 0, type: ShaderValueType.Float),
                        Pad(offset: 4),
                        Pad(offset: 8),
                        Pad(offset: 12),
                        Member(name: "tint", offset: 16, type: ShaderValueType.Float3),
                        Member(name: "cellSize", offset: 28, type: ShaderValueType.Float),
                        Member(name: "seed", offset: 32, type: ShaderValueType.Uint),
                        Member(name: "flickerTicks", offset: 36, type: ShaderValueType.Uint),
                    ],
                    Name: "passGroup",
                    Set: 3
                ),
                new ShaderInterfaceBinding(
                    Binding: 1,
                    Kind: GpuBindingKind.SampledImage,
                    Members: [],
                    Name: "source",
                    Set: 3
                ),
                new ShaderInterfaceBinding(
                    Binding: 2,
                    Kind: GpuBindingKind.Sampler,
                    Members: [],
                    Name: "sourceSampler",
                    Set: 3
                ),
            ]
        );
        Assert.Equal(
            actual: ShaderInterfaceSpike.Pixelate.Layout().Groups.Select(selector: static group => group.BlockSizeBytes),
            expected: [16u, 64u]
        );
        Assert.Equal(
            actual: ShaderInterfaceSpike.Pixelate.Layout().Bindings[1].Members,
            expected: [
                Member(name: "cellSize", offset: 0, type: ShaderValueType.Uint),
                Pad(offset: 4),
                Pad(offset: 8),
                Pad(offset: 12),
                Member(length: 3, name: "channelLevels", offset: 16, type: ShaderValueType.Uint4),
            ]
        );
    }
    [Fact]
    public void A_group_without_values_numbers_its_bindings_from_zero() {
        var layout = new ShaderInterface(
            members: [
                ShaderInterfaceMember.SampledImage(
                    group: ShaderInterfaceGroup.World,
                    name: "lattice",
                    type: ShaderValueType.Float4
                ),
                ShaderInterfaceMember.Sampler(
                    group: ShaderInterfaceGroup.World,
                    name: "latticeSampler"
                ),
            ],
            name: "lattice-only"
        ).Layout();

        Assert.Equal(
            actual: layout.Bindings.Select(selector: static binding => (binding.Set, binding.Binding, binding.Kind)),
            expected: [(1u, 0u, GpuBindingKind.SampledImage), (1u, 1u, GpuBindingKind.Sampler)]
        );
    }
    [Fact]
    public void The_document_round_trips_and_its_hash_follows_its_content() {
        var original = ShaderInterfaceSpike.Pixelate;
        var parsed = ShaderInterface.Parse(json: original.ToJson());

        Assert.Equal(
            actual: parsed.Members,
            expected: original.Members
        );
        Assert.Equal(
            actual: parsed.ToJson(),
            expected: original.ToJson()
        );
        Assert.Equal(
            actual: parsed.Hash,
            expected: original.Hash
        );
        Assert.Contains(
            expectedSubstring: "\"kind\":\"StorageImage\",\"type\":\"float4\",\"format\":\"R8G8B8A8Unorm\"",
            actualString: original.ToJson()
        );

        var reordered = new ShaderInterface(
            members: [.. original.Members.Reverse()],
            name: original.Name
        );

        Assert.NotEqual(
            actual: reordered.Hash,
            expected: original.Hash
        );
        Assert.NotEqual(
            actual: ShaderInterfaceSpike.FilmGrain.Hash,
            expected: original.Hash
        );
    }
    [InlineData("{\"name\":\"a\",\"members\":[{\"name\":\"x\",\"group\":0,\"kind\":\"Value\",\"type\":\"float\"}]}")]
    [InlineData("{\"name\":\"a\",\"members\":[{\"name\":\"x\",\"group\":\"Frame\",\"kind\":\"Scalar\",\"type\":\"float\"}]}")]
    [InlineData("{\"name\":\"a\",\"members\":[{\"name\":\"x\",\"group\":\"Frame\",\"kind\":\"Value\",\"type\":\"float\",\"offset\":4}]}")]
    [InlineData("{\"name\":\"a\",\"members\":[{\"name\":\"x\",\"group\":\"Frame\",\"type\":\"float\"}]}")]
    [InlineData("{\"name\":\"a\",\"members\":[{\"name\":\"x\",\"group\":\"Pass\",\"kind\":\"StorageImage\",\"type\":\"float4\",\"format\":1}]}")]
    [InlineData("{\"name\":\"a\",\"members\":[{\"name\":\"x\",\"group\":\"Frame\",\"kind\":\"Value\",\"type\":\"float\"}],\"name\":\"b\"}")]
    [Theory]
    public void The_document_refuses_numbers_unknown_names_unknown_and_missing_properties(string json) {
        _ = Assert.Throws<InvalidDataException>(testCode: () => ShaderInterface.Parse(json: json));
    }
    [Fact]
    public void The_interface_refuses_what_it_cannot_generate() {
        static void Refused(string name, ShaderInterfaceMember[] members) =>
            _ = Assert.Throws<InvalidDataException>(testCode: () => new ShaderInterface(
                members: members,
                name: name
            ));

        Refused(members: [ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: "s")], name: "Upper");
        Refused(members: [], name: "empty");
        Refused(members: [ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: "s"), ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Frame, name: "s")], name: "twice");
        Refused(members: [ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: "_pad4")], name: "reserved");
        Refused(members: [ShaderInterfaceMember.Array(group: ShaderInterfaceGroup.Pass, length: 2, name: "levels", type: ShaderValueType.Uint), ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: "levelsAt")], name: "accessor");
        Refused(members: [ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, name: "x", type: ShaderValueType.Float), ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: "passGroup")], name: "block");
        Refused(members: [ShaderInterfaceMember.StorageImage(format: GpuPixelFormat.B8G8R8A8Unorm, group: ShaderInterfaceGroup.Pass, name: "o", type: ShaderValueType.Float4)], name: "bgra");
        Refused(members: [ShaderInterfaceMember.Array(group: ShaderInterfaceGroup.Pass, length: 0, name: "a", type: ShaderValueType.Float)], name: "empty-array");
        Refused(members: [new ShaderInterfaceMember(Group: ShaderInterfaceGroup.Pass, Kind: ShaderInterfaceMemberKind.Sampler, Name: "s", Type: ShaderValueType.Float)], name: "typed-sampler");
        Refused(members: [new ShaderInterfaceMember(Group: ShaderInterfaceGroup.Pass, Kind: ShaderInterfaceMemberKind.Value, Name: "v")], name: "untyped-value");
        Refused(members: [new ShaderInterfaceMember(Group: ShaderInterfaceGroup.Pass, Kind: ShaderInterfaceMemberKind.StorageImage, Name: "o", Type: ShaderValueType.Float4)], name: "unformatted");
    }
    [Fact]
    public void The_generated_include_is_the_pinned_text_for_its_interface() {
        var shaderInterface = ShaderInterfaceSpike.Pixelate;

        Assert.Equal(
            actual: ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface),
            expected: $$"""
                // Generated from shader interface 'pixelate' ({{shaderInterface.Hash}}). Regenerate it from the interface; never edit it.
                #ifndef PUCK_SHADER_INTERFACE_PIXELATE
                #define PUCK_SHADER_INTERFACE_PIXELATE

                // The Frame group: descriptor set 0, register space 0.
                struct PixelateFrame {
                    [[vk::offset(0)]] uint tick;
                    [[vk::offset(4)]] uint _pad4;
                    [[vk::offset(8)]] uint2 extent;
                };
                [[vk::binding(0, 0)]] ConstantBuffer<PixelateFrame> frameGroup : register(b0, space0);

                // The Pass group: descriptor set 3, register space 3.
                struct PixelatePass {
                    [[vk::offset(0)]] uint cellSize;
                    [[vk::offset(4)]] uint _pad4;
                    [[vk::offset(8)]] uint _pad8;
                    [[vk::offset(12)]] uint _pad12;
                    [[vk::offset(16)]] uint4 channelLevels[3];
                };
                [[vk::binding(0, 3)]] ConstantBuffer<PixelatePass> passGroup : register(b0, space3);
                [[vk::binding(1, 3)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> output : register(u1, space3);
                [[vk::binding(2, 3)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> source : register(u2, space3);

                uint channelLevelsAt(uint index) { return passGroup.channelLevels[index].x; }

                #endif // PUCK_SHADER_INTERFACE_PIXELATE

                """.ReplaceLineEndings(replacementText: "\n")
        );
        Assert.Equal(
            actual: ShaderInterfaceHlsl.Generate(shaderInterface: ShaderInterface.Parse(json: shaderInterface.ToJson())),
            expected: ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface)
        );
    }
}
