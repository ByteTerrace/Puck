using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>The type of one manifest-declared value — a push-constant field or a config field — in HLSL's own
/// spelling (<c>float</c>, <c>float2</c>, <c>uint</c>, <c>int4</c>, …), which is each member's JSON string. Every type
/// is one to four 32-bit components; the component kind (<see cref="ShaderScalarKind"/>) and count derive from the
/// value.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderValueType>))]
public enum ShaderValueType {
    /// <summary>One 32-bit float.</summary>
    [JsonStringEnumMemberName(name: "float")]
    Float = 0x11,
    /// <summary>Two 32-bit floats.</summary>
    [JsonStringEnumMemberName(name: "float2")]
    Float2 = 0x12,
    /// <summary>Three 32-bit floats.</summary>
    [JsonStringEnumMemberName(name: "float3")]
    Float3 = 0x13,
    /// <summary>Four 32-bit floats.</summary>
    [JsonStringEnumMemberName(name: "float4")]
    Float4 = 0x14,
    /// <summary>One unsigned 32-bit integer.</summary>
    [JsonStringEnumMemberName(name: "uint")]
    Uint = 0x21,
    /// <summary>Two unsigned 32-bit integers.</summary>
    [JsonStringEnumMemberName(name: "uint2")]
    Uint2 = 0x22,
    /// <summary>Three unsigned 32-bit integers.</summary>
    [JsonStringEnumMemberName(name: "uint3")]
    Uint3 = 0x23,
    /// <summary>Four unsigned 32-bit integers.</summary>
    [JsonStringEnumMemberName(name: "uint4")]
    Uint4 = 0x24,
    /// <summary>One signed 32-bit integer.</summary>
    [JsonStringEnumMemberName(name: "int")]
    Int = 0x31,
    /// <summary>Two signed 32-bit integers.</summary>
    [JsonStringEnumMemberName(name: "int2")]
    Int2 = 0x32,
    /// <summary>Three signed 32-bit integers.</summary>
    [JsonStringEnumMemberName(name: "int3")]
    Int3 = 0x33,
    /// <summary>Four signed 32-bit integers.</summary>
    [JsonStringEnumMemberName(name: "int4")]
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
    /// <summary>Gets the value type with <paramref name="count"/> components of <paramref name="kind"/>.</summary>
    /// <param name="kind">The component kind.</param>
    /// <param name="count">The component count, one to four.</param>
    /// <returns>The value type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined kind, or
    /// <paramref name="count"/> is not one to four.</exception>
    public static ShaderValueType FromComponents(ShaderScalarKind kind, uint count) {
        if (!Enum.IsDefined(value: kind)) {
            throw new ArgumentOutOfRangeException(
                actualValue: kind,
                message: "The component kind is not defined.",
                paramName: nameof(kind)
            );
        }

        ArgumentOutOfRangeException.ThrowIfZero(value: count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: 4u,
            value: count
        );

        return ((ShaderValueType)((((uint)kind) << 4) | count));
    }
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
            _ => throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            "The value type is not defined."
        ),
        };
        var count = type.ComponentCount();

        return ((count == 1)
            ? kind
            : $"{kind}{count}"
        );
    }
}
