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
    public void A_buffers_element_is_optional_and_never_three_components() {
        var typed = ShaderInterfaceMember.ReadOnlyBuffer(
            element: ShaderValueType.Float2,
            group: ShaderInterfaceGroup.Pass,
            name: "points"
        );

        Assert.Equal(
            actual: typed.Type,
            expected: ShaderValueType.Float2
        );
        Assert.Null(@object: ShaderInterfaceMember.ReadWriteBuffer(group: ShaderInterfaceGroup.Pass, name: "raw").Type);

        foreach (var element in ((ShaderValueType[])[ShaderValueType.Float3, ShaderValueType.Uint3, ShaderValueType.Int3])) {
            foreach (var member in ((ShaderInterfaceMember[])[
                ShaderInterfaceMember.ReadOnlyBuffer(element: element, group: ShaderInterfaceGroup.Pass, name: "points"),
                ShaderInterfaceMember.ReadWriteBuffer(element: element, group: ShaderInterfaceGroup.Pass, name: "points"),
            ])) {
                Assert.Contains(
                    actualString: Assert.Throws<InvalidDataException>(testCode: () => new ShaderInterface(
                        members: [member],
                        name: "three"
                    )).Message,
                    expectedSubstring: $"member 'points': a buffer's element cannot be the three-component {element.Spelling()}"
                );
            }
        }
    }
    [Fact]
    public void A_member_cannot_take_the_pushed_index_s_generated_name() {
        Assert.Contains(
            actualString: Assert.Throws<InvalidDataException>(testCode: () => new ShaderInterface(
                members: [ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.Pass, name: ShaderInterface.PushedIndexVariableName)],
                name: "shadowed",
                pushesIndex: true
            )).Message,
            expectedSubstring: "declares 'pushedIndex', which its generated pushed index declares"
        );
    }
    [Fact]
    public void The_pushed_index_and_typed_buffers_round_trip_and_version_the_hash() {
        var original = ShaderInterfaceSpike.TypedBuffers;
        var json = original.ToJson();
        var parsed = ShaderInterface.Parse(json: json);
        var unpushed = new ShaderInterface(
            members: original.Members,
            name: original.Name
        );

        Assert.True(condition: parsed.PushesIndex);
        Assert.Equal(
            actual: parsed.Members,
            expected: original.Members
        );
        Assert.Equal(
            actual: parsed.ToJson(),
            expected: json
        );
        Assert.EndsWith(
            actualString: json,
            expectedEndString: ",\"pushesIndex\":true}"
        );
        Assert.Contains(
            actualString: json,
            expectedSubstring: "{\"name\":\"wide\",\"group\":\"Pass\",\"kind\":\"ReadOnlyBuffer\",\"type\":\"float4\"}"
        );
        Assert.DoesNotContain(
            actualString: unpushed.ToJson(),
            expectedSubstring: "pushesIndex"
        );
        Assert.NotEqual(
            actual: unpushed.Hash,
            expected: original.Hash
        );
    }
    [Fact]
    public void Each_backends_view_carries_its_buffer_strides_and_its_pushed_index() {
        var layout = ShaderInterfaceSpike.TypedBuffers.Layout();

        static IEnumerable<(string Name, uint Set, uint Binding, bool Pushed, uint Stride)> Describe(IReadOnlyList<ShaderInterfaceBinding> bindings) =>
            bindings.Select(selector: static binding => (binding.Name, binding.Set, binding.Binding, binding.Pushed, binding.ElementStride));

        Assert.Equal(
            actual: Describe(bindings: layout.Bindings),
            expected: [
                ("frameGroup", 0u, 0u, false, 0u),
                ("pushedIndex", 0u, 0u, true, 0u),
                ("narrow", 3u, 0u, false, 4u),
                ("wide", 3u, 1u, false, 16u),
                ("raw", 3u, 2u, false, ShaderInterfaceLayout.SpirvRawBufferStride),
                ("output", 3u, 3u, false, 8u),
                ("rawOutput", 3u, 4u, false, ShaderInterfaceLayout.SpirvRawBufferStride),
            ]
        );
        Assert.Equal(
            actual: Describe(bindings: layout.DxilBindings),
            expected: [
                ("frameGroup", 0u, 0u, false, 0u),
                ("narrow", 3u, 0u, false, 4u),
                ("wide", 3u, 1u, false, 16u),
                ("raw", 3u, 2u, false, ShaderInterfaceLayout.DxilRawBufferStride),
                ("output", 3u, 3u, false, 8u),
                ("rawOutput", 3u, 4u, false, ShaderInterfaceLayout.DxilRawBufferStride),
                ("pushedIndex", GpuPipelineLayoutDescription.PushIndexSpace, 0u, false, 0u),
            ]
        );
        Assert.Equal(
            actual: layout.DxilBindings[^1].Members,
            expected: [Member(name: "index", offset: 0, type: ShaderValueType.Uint)]
        );
        Assert.Null(@object: layout.Mismatch(reflected: layout.Bindings));
        Assert.Null(@object: layout.Mismatch(reflected: layout.DxilBindings));
    }
    [Fact]
    public void A_bound_frame_block_and_a_pushed_index_are_told_apart() {
        // SPIR-V reflects both at set 0, binding 0: the frame group's block bound, the index pushed.
        var layout = ShaderInterfaceSpike.TypedBuffers.Layout();
        var frame = layout.Bindings.Single(predicate: static binding => string.Equals(
            a: binding.Name,
            b: "frameGroup",
            comparisonType: StringComparison.Ordinal
        ));
        var index = layout.Bindings.Single(predicate: static binding => binding.Pushed);

        Assert.Null(@object: layout.Mismatch(reflected: [frame, index]));
        Assert.StartsWith(
            actualString: layout.Mismatch(reflected: [frame with { Pushed = true }]),
            expectedStartString: "the module reads frameGroup set 0 binding 0 pushed ConstantBuffer ["
        );
        Assert.StartsWith(
            actualString: layout.Mismatch(reflected: [index with { Pushed = false }]),
            expectedStartString: "the module reads pushedIndex set 0 binding 0 ConstantBuffer [index@0:uint];"
        );
    }
    [Fact]
    public void The_generated_include_declares_typed_and_raw_buffers_and_the_pushed_index() {
        var shaderInterface = ShaderInterfaceSpike.TypedBuffers;

        Assert.Equal(
            actual: ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface),
            expected: $$"""
                // Generated from shader interface 'typed-buffers' ({{shaderInterface.Hash}}). Regenerate it from the interface; never edit it.
                #ifndef PUCK_SHADER_INTERFACE_TYPED_BUFFERS
                #define PUCK_SHADER_INTERFACE_TYPED_BUFFERS

                // The Frame group: descriptor set 0, register space 0.
                struct TypedBuffersFrame {
                    [[vk::offset(0)]] uint tick;
                    [[vk::offset(4)]] uint _pad4;
                    [[vk::offset(8)]] uint2 extent;
                };
                [[vk::binding(0, 0)]] ConstantBuffer<TypedBuffersFrame> frameGroup : register(b0, space0);

                // The Pass group: descriptor set 3, register space 3.
                [[vk::binding(0, 3)]] StructuredBuffer<uint> narrow : register(t0, space3);
                [[vk::binding(1, 3)]] StructuredBuffer<float4> wide : register(t1, space3);
                [[vk::binding(2, 3)]] ByteAddressBuffer raw : register(t2, space3);
                [[vk::binding(3, 3)]] RWStructuredBuffer<uint2> output : register(u3, space3);
                [[vk::binding(4, 3)]] RWByteAddressBuffer rawOutput : register(u4, space3);

                // The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
                struct TypedBuffersPushedIndex {
                    [[vk::offset(0)]] uint index;
                };
                [[vk::push_constant]] ConstantBuffer<TypedBuffersPushedIndex> pushedIndex : register(b0, space4);

                #endif // PUCK_SHADER_INTERFACE_TYPED_BUFFERS

                """.ReplaceLineEndings(replacementText: "\n")
        );
        Assert.Equal(
            actual: ShaderInterface.PushedIndexTypeName(interfaceName: "sdf-bricks"),
            expected: "SdfBricksPushedIndex"
        );
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
