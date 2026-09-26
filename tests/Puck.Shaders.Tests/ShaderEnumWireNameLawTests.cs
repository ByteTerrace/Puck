using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Puck.Shaders.Tests;

/// <summary>
/// Every enum a shader-set or probe-kind manifest carries crosses the wire through the one strict enum converter, by
/// the name its member declares. For each enum the table names every member's JSON string; the law writes each member
/// through the manifest's own source-generated context, reads the string back to the same member, and checks the table
/// covers the enum exactly, so a member added without a wire name, or a name that changes, turns it red. A number or an
/// unknown name is refused.
/// </summary>
public sealed class ShaderEnumWireNameLawTests {
    private static void AssertWireNames<TEnum>(JsonTypeInfo<TEnum> typeInfo, IReadOnlyDictionary<TEnum, string> expected) where TEnum : struct, Enum {
        Assert.Equal(
            actual: expected.Keys.Order(),
            expected: Enum.GetValues<TEnum>().Order()
        );

        foreach (var (member, name) in expected) {
            var json = JsonSerializer.Serialize(
                jsonTypeInfo: typeInfo,
                value: member
            );

            Assert.Equal(
                actual: json,
                expected: $"\"{name}\""
            );
            Assert.Equal(
                actual: JsonSerializer.Deserialize(
                    json: json,
                    jsonTypeInfo: typeInfo
                ),
                expected: member
            );
        }

        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: "0",
            jsonTypeInfo: typeInfo
        ));
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: "\"unknownMember\"",
            jsonTypeInfo: typeInfo
        ));
    }

    [Fact]
    public void AShaderValueTypeCrossesTheWireAsItsHlslSpelling() {
        var expected = new Dictionary<ShaderValueType, string> {
            [ShaderValueType.Float] = "float",
            [ShaderValueType.Float2] = "float2",
            [ShaderValueType.Float3] = "float3",
            [ShaderValueType.Float4] = "float4",
            [ShaderValueType.Uint] = "uint",
            [ShaderValueType.Uint2] = "uint2",
            [ShaderValueType.Uint3] = "uint3",
            [ShaderValueType.Uint4] = "uint4",
            [ShaderValueType.Int] = "int",
            [ShaderValueType.Int2] = "int2",
            [ShaderValueType.Int3] = "int3",
            [ShaderValueType.Int4] = "int4",
        };

        AssertWireNames(
            expected: expected,
            typeInfo: ProbeKindManifestJsonContext.Default.ShaderValueType
        );
        // The diagnostic spelling and the wire name are one vocabulary.
        Assert.All(
            action: static pair => Assert.Equal(
                actual: pair.Key.Spelling(),
                expected: pair.Value
            ),
            collection: expected
        );
    }
    [Fact]
    public void AProbeSocketClassCrossesTheWireInLowerCamelCase() => AssertWireNames(
        expected: new Dictionary<ProbeSocketClass, string> {
            [ProbeSocketClass.Frame] = "frame",
            [ProbeSocketClass.StrobePair] = "strobePair",
        },
        typeInfo: ProbeKindManifestJsonContext.Default.ProbeSocketClass
    );
    [Fact]
    public void AProbeKindClassCrossesTheWireInLowerCamelCase() => AssertWireNames(
        expected: new Dictionary<ProbeKindClass, string> {
            [ProbeKindClass.Kernel] = "kernel",
            [ProbeKindClass.Model] = "model",
        },
        typeInfo: ProbeKindManifestJsonContext.Default.ProbeKindClass
    );
}
