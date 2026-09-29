using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders.Tests;

/// <summary>The native record is the single layout source; generated fields, stride and reflected identity follow it.</summary>
public sealed class ShaderInterfaceStructureLawTests {
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public readonly record struct LightRecord {
        [FieldOffset(0)] public readonly Vector3 Direction;
        [FieldOffset(12)] public readonly float Weight;
        [FieldOffset(16)] public readonly Vector3 Color;
        [FieldOffset(28)] public readonly uint Kind;
        [FieldOffset(32)] public readonly float Radius;
        [FieldOffset(36)] public readonly int Slot;
    }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public readonly record struct MisalignedRecord {
        [FieldOffset(0)] public readonly float Weight;
        [FieldOffset(4)] public readonly Vector3 Direction;
    }

    [Fact]
    public void Sky_and_light_native_records_have_backend_aligned_strides() {
        var records = new[] {
            ShaderInterfaceStructure.From<SdfLightData>(),
            ShaderInterfaceStructure.From<SdfLightFrameData>(),
            ShaderInterfaceStructure.From<SdfSkyStopData>(),
            ShaderInterfaceStructure.From<SdfSoftboxData>(),
            ShaderInterfaceStructure.From<SdfSkyFrameData>(),
        };

        Assert.Equal(expected: [48u, 16u, 16u, 48u, 208u], actual: records.Select(selector: static record => record.SizeBytes));
        var light = records[0];

        Assert.Equal(expected: [0u, 12u, 16u, 28u, 32u, 36u, 40u, 44u],
            actual: light.Members.Select(selector: static member => member.Offset));
        Assert.Equal(expected: ShaderValueType.Uint, actual: light.Members.Single(predicate: static member => (member.Name == "Kind")).Type);
        Assert.Equal(expected: ShaderValueType.Int, actual: light.Members.Single(predicate: static member => (member.Name == "DynamicSlot")).Type);
    }

    private static ShaderInterface Interface(ShaderInterfaceStructure structure) => new(
        name: "record-law",
        members: [
            ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: "lights", structure: structure),
            ShaderInterfaceMember.ReadWriteBuffer(element: ShaderValueType.Float4, group: ShaderInterfaceGroup.Pass, name: "result"),
        ]
    );

    [Fact]
    public void Native_fields_round_trip_with_explicit_padding_and_one_stride() {
        var structure = ShaderInterfaceStructure.From<LightRecord>();

        Assert.Equal(expected: 48u, actual: structure.SizeBytes);
        Assert.Equal(expected: [0u, 12u, 16u, 28u, 32u, 36u], actual: structure.Members.Select(selector: static member => member.Offset));
        var shader = Interface(structure: structure);
        var parsed = ShaderInterface.Parse(json: shader.ToJson());

        Assert.Equal(expected: shader.Members, actual: parsed.Members);
        Assert.Equal(expected: shader.Hash, actual: parsed.Hash);
        Assert.Equal(expected: 48u, actual: shader.Layout().Bindings[0].ElementStride);
        Assert.Equal(expected: shader.Layout().Bindings, actual: shader.Layout().DxilBindings);
        var hlsl = ShaderInterfaceHlsl.Generate(shaderInterface: shader);

        Assert.Contains(actualString: hlsl, expectedSubstring: "[[vk::offset(16)]] float3 Color;");
        Assert.Contains(actualString: hlsl, expectedSubstring: "[[vk::offset(40)]] uint _pad40;");
        Assert.Contains(actualString: hlsl, expectedSubstring: "[[vk::offset(44)]] uint _pad44;");
        Assert.Contains(actualString: hlsl, expectedSubstring: "StructuredBuffer<LightRecord> lightsLayout");
    }
    [Fact]
    public void Same_stride_reordered_fields_change_the_reflected_identity() {
        var structure = ShaderInterfaceStructure.From<LightRecord>();
        var changed = new ShaderInterfaceStructure(name: structure.Name, sizeBytes: structure.SizeBytes,
            members: structure.Members.Select(selector: static member => member with {
                Name = member.Name switch { "Direction" => "Color", "Color" => "Direction", _ => member.Name },
            }).ToArray());
        var before = Interface(structure: structure).Layout();
        var after = Interface(structure: changed).Layout();

        Assert.Equal(expected: before.Bindings[0].ElementStride, actual: after.Bindings[0].ElementStride);
        Assert.NotEqual(expected: before.Bindings[0].Name, actual: after.Bindings[0].Name);
        Assert.NotNull(@object: before.Mismatch(reflected: after.Bindings));
    }
    [Fact]
    public void Native_fields_that_disagree_with_shader_alignment_refuse_by_name() {
        var refusal = Assert.Throws<InvalidDataException>(testCode: () => ShaderInterfaceStructure.From<MisalignedRecord>());

        Assert.Contains(expectedSubstring: "Direction", actualString: refusal.Message);
        Assert.Contains(expectedSubstring: "16-byte alignment", actualString: refusal.Message);
    }
    [InlineData("lights")]
    [InlineData("passGroup")]
    [Theory]
    public void Native_field_colliding_with_a_generated_alias_refuses_by_name(string field) {
        var structure = new ShaderInterfaceStructure(name: "AliasedRecord", sizeBytes: 4,
            members: [new ShaderInterfaceBlockMember(Length: 0, Name: field, Offset: 0, Type: ShaderValueType.Float)]);
        var refusal = Assert.Throws<InvalidDataException>(testCode: () => new ShaderInterface(
            name: "record-alias", stamp: "Alpha", members: [
                ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: "lights", structure: structure),
                ShaderInterfaceMember.Value(name: "amount", group: ShaderInterfaceGroup.Pass, type: ShaderValueType.Float),
            ]));

        Assert.Contains(expectedSubstring: $"field '{field}'", actualString: refusal.Message);
        Assert.Contains(expectedSubstring: "generated alias", actualString: refusal.Message);
    }
    [Fact]
    public void Record_echo_reads_two_distinct_native_elements_and_leaves_padding_zero() {
        var shader = ShaderInterfaceEcho.InterfaceOf(shaderInterface: Interface(structure: ShaderInterfaceStructure.From<LightRecord>()));
        var resource = shader.Layout().Groups[0].Resources[0];
        var bytes = new byte[96];

        ShaderInterfaceEcho.WriteRecordSentinels(buffer: bytes, resource: resource, set: 1);
        Assert.Equal(expected: 12u, actual: ShaderInterfaceEcho.Width(shaderInterface: shader));
        Assert.NotEqual(expected: BitConverter.ToUInt32(startIndex: 0, value: bytes), actual: BitConverter.ToUInt32(startIndex: 48, value: bytes));
        Assert.Equal(expected: new byte[8], actual: bytes[40..48]);
        Assert.Equal(expected: new byte[8], actual: bytes[88..96]);
        var source = ShaderInterfaceEcho.Generate(shaderInterface: shader);

        Assert.Contains(actualString: source, expectedSubstring: "lights[0].Direction.x");
        Assert.Contains(actualString: source, expectedSubstring: "lights[1].Slot");
    }
    [Fact]
    public async Task Generated_native_record_compiles_with_the_same_stride_and_identity_on_both_backends() {
        Assert.SkipWhen(condition: (ShaderInterfaceSpike.Dxc is null), reason: "DXC is required for record reflection.");
        var shader = Interface(structure: ShaderInterfaceStructure.From<LightRecord>());
        var source = (ShaderInterfaceHlsl.Generate(shaderInterface: shader) + "\n[numthreads(1, 1, 1)] void CSMain(uint3 id : SV_DispatchThreadID) { LightRecord light = lights[id.x]; result[id.x] = float4(light.Direction + light.Color, light.Weight + light.Radius + light.Kind + light.Slot); }");
        var build = await ShaderInterfaceSpike.CompileSourceAsync(source: source, entryPoint: "CSMain", profile: "cs_6_6",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: shader.Layout().Bindings, actual: SpirvInterfaceReader.Read(module: build.Spirv));
        if (OperatingSystem.IsWindows()) {
            using var reader = DxilInterfaceReader.Load(toolchain: new ShaderToolchain());

            Assert.Equal(expected: shader.Layout().DxilBindings, actual: reader.Read(container: build.Dxil));
        }
    }
}
