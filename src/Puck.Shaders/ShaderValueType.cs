using System.Text.Json.Serialization;

namespace Puck.Shaders;

/// <summary>The type of one manifest-declared value — a push-constant field or a config field — in HLSL's own
/// spelling (<c>float</c>, <c>float2</c>, <c>uint</c>, <c>int4</c>, …), via
/// <see cref="ShaderValueTypeJsonConverter"/>. Every type is one to four 32-bit components; the component kind
/// (<see cref="ShaderScalarKind"/>) and count derive from the value.</summary>
[JsonConverter(typeof(ShaderValueTypeJsonConverter))]
public enum ShaderValueType {
    /// <summary>One 32-bit float.</summary>
    Float = 0x11,
    /// <summary>Two 32-bit floats.</summary>
    Float2 = 0x12,
    /// <summary>Three 32-bit floats.</summary>
    Float3 = 0x13,
    /// <summary>Four 32-bit floats.</summary>
    Float4 = 0x14,
    /// <summary>One unsigned 32-bit integer.</summary>
    Uint = 0x21,
    /// <summary>Two unsigned 32-bit integers.</summary>
    Uint2 = 0x22,
    /// <summary>Three unsigned 32-bit integers.</summary>
    Uint3 = 0x23,
    /// <summary>Four unsigned 32-bit integers.</summary>
    Uint4 = 0x24,
    /// <summary>One signed 32-bit integer.</summary>
    Int = 0x31,
    /// <summary>Two signed 32-bit integers.</summary>
    Int2 = 0x32,
    /// <summary>Three signed 32-bit integers.</summary>
    Int3 = 0x33,
    /// <summary>Four signed 32-bit integers.</summary>
    Int4 = 0x34,
}
/// <summary>The component kind of a <see cref="ShaderValueType"/>.</summary>
public enum ShaderScalarKind {
    /// <summary>A 32-bit float component.</summary>
    Float = 1,
    /// <summary>An unsigned 32-bit integer component.</summary>
    Uint = 2,
    /// <summary>A signed 32-bit integer component.</summary>
    Int = 3,
}
/// <summary>Derived facts about a <see cref="ShaderValueType"/>.</summary>
public static class ShaderValueTypes {
    /// <summary>The byte size of one component; every type is built from 32-bit components.</summary>
    public const uint ComponentBytes = 4;

    /// <summary>Gets the number of 32-bit components (1..4).</summary>
    /// <param name="type">The value type.</param>
    /// <returns>The component count.</returns>
    public static uint ComponentCount(this ShaderValueType type) => ((uint)type) & 0xF;
    /// <summary>Gets the component kind.</summary>
    /// <param name="type">The value type.</param>
    /// <returns>The scalar kind.</returns>
    public static ShaderScalarKind ScalarKind(this ShaderValueType type) => ((ShaderScalarKind)(((uint)type) >> 4));
    /// <summary>Gets the byte size of a whole value (<see cref="ComponentCount"/> × <see cref="ComponentBytes"/>).</summary>
    /// <param name="type">The value type.</param>
    /// <returns>The byte size.</returns>
    public static uint SizeBytes(this ShaderValueType type) => (type.ComponentCount() * ComponentBytes);
    /// <summary>Gets the HLSL spelling (<c>float</c>, <c>uint2</c>, …), which is also the manifest's JSON string.</summary>
    /// <param name="type">The value type.</param>
    /// <returns>The spelling.</returns>
    public static string Spelling(this ShaderValueType type) {
        var kind = type.ScalarKind() switch {
            ShaderScalarKind.Float => "float",
            ShaderScalarKind.Uint => "uint",
            ShaderScalarKind.Int => "int",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The value type is not defined."),
        };
        var count = type.ComponentCount();

        return ((count == 1) ? kind : $"{kind}{count}");
    }
    /// <summary>Parses an HLSL spelling back into a <see cref="ShaderValueType"/>.</summary>
    /// <param name="spelling">The spelling.</param>
    /// <param name="type">The parsed type, when the spelling is recognized.</param>
    /// <returns><see langword="true"/> when recognized.</returns>
    public static bool TryParse(string? spelling, out ShaderValueType type) {
        type = spelling switch {
            "float" => ShaderValueType.Float,
            "float2" => ShaderValueType.Float2,
            "float3" => ShaderValueType.Float3,
            "float4" => ShaderValueType.Float4,
            "uint" => ShaderValueType.Uint,
            "uint2" => ShaderValueType.Uint2,
            "uint3" => ShaderValueType.Uint3,
            "uint4" => ShaderValueType.Uint4,
            "int" => ShaderValueType.Int,
            "int2" => ShaderValueType.Int2,
            "int3" => ShaderValueType.Int3,
            "int4" => ShaderValueType.Int4,
            _ => default,
        };

        return (type != default);
    }
}
