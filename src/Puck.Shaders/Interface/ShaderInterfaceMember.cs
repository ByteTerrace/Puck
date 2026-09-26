using System.Text.Json.Serialization;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>One named member of a <see cref="ShaderInterface"/>: a value, an array, an image, a buffer or a sampler, in one
/// frequency group.</summary>
/// <param name="Name">The member's name, which is also its HLSL identifier: an ASCII letter followed by ASCII letters
/// and digits.</param>
/// <param name="Group">The frequency group the member belongs to, which fixes its descriptor set and register
/// space.</param>
/// <param name="Kind">What the member is.</param>
/// <param name="Type">The value type of a <see cref="ShaderInterfaceMemberKind.Value"/>, the element type of an
/// <see cref="ShaderInterfaceMemberKind.Array"/>, the texel type an image reads as, or the element type of a structured
/// buffer; <see langword="null"/> for a raw buffer or a <see cref="ShaderInterfaceMemberKind.Sampler"/>.</param>
/// <param name="Length">The element count of an <see cref="ShaderInterfaceMemberKind.Array"/>, at least one;
/// <see langword="null"/> for every other kind.</param>
/// <param name="Format">The texel format of a <see cref="ShaderInterfaceMemberKind.StorageImage"/>;
/// <see langword="null"/> for every other kind.</param>
public sealed record ShaderInterfaceMember(
    string Name,
    ShaderInterfaceGroup Group,
    ShaderInterfaceMemberKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ShaderValueType? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Length = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GpuPixelFormat? Format = null
) {
    /// <summary>Creates a scalar or vector member.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="type">The value type.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember Value(string name, ShaderInterfaceGroup group, ShaderValueType type) =>
        new(
            Group: group,
            Kind: ShaderInterfaceMemberKind.Value,
            Name: name,
            Type: type
        );
    /// <summary>Creates a fixed-length array member: a read-only structured buffer of its element type, which a pass
    /// reads through its generated accessor.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="type">The element type.</param>
    /// <param name="length">The element count, at least one.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember Array(string name, ShaderInterfaceGroup group, ShaderValueType type, uint length) =>
        new(
            Group: group,
            Kind: ShaderInterfaceMemberKind.Array,
            Length: length,
            Name: name,
            Type: type
        );
    /// <summary>Creates a sampled image member.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="type">The texel type a sample returns.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember SampledImage(string name, ShaderInterfaceGroup group, ShaderValueType type) =>
        new(
            Group: group,
            Kind: ShaderInterfaceMemberKind.SampledImage,
            Name: name,
            Type: type
        );
    /// <summary>Creates a sampler member.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember Sampler(string name, ShaderInterfaceGroup group) =>
        new(
            Group: group,
            Kind: ShaderInterfaceMemberKind.Sampler,
            Name: name
        );
    /// <summary>Creates a storage image member.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="type">The texel type a load returns and a store takes.</param>
    /// <param name="format">The texel format in memory.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember StorageImage(string name, ShaderInterfaceGroup group, ShaderValueType type, GpuPixelFormat format) =>
        new(
            Format: format,
            Group: group,
            Kind: ShaderInterfaceMemberKind.StorageImage,
            Name: name,
            Type: type
        );
    /// <summary>Creates a buffer member a pass reads: a structured buffer of <paramref name="element"/>, or a raw buffer
    /// read by byte address when there is no element.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="element">The element type, a scalar or a two- or four-component vector, or <see langword="null"/>
    /// for a raw buffer.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember ReadOnlyBuffer(string name, ShaderInterfaceGroup group, ShaderValueType? element = null) =>
        new(
            Group: group,
            Kind: ShaderInterfaceMemberKind.ReadOnlyBuffer,
            Name: name,
            Type: element
        );
    /// <summary>Creates a buffer member a pass reads and writes: a structured buffer of <paramref name="element"/>, or a
    /// raw buffer read and written by byte address when there is no element.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="element">The element type, a scalar or a two- or four-component vector, or <see langword="null"/>
    /// for a raw buffer.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember ReadWriteBuffer(string name, ShaderInterfaceGroup group, ShaderValueType? element = null) =>
        new(
            Group: group,
            Kind: ShaderInterfaceMemberKind.ReadWriteBuffer,
            Name: name,
            Type: element
        );

    /// <summary>Gets a value indicating whether the member lives in its group's constant block: a value does, and every
    /// other kind, an array included, is a binding of its own.</summary>
    [JsonIgnore]
    public bool IsBlockMember => (Kind == ShaderInterfaceMemberKind.Value);
}
