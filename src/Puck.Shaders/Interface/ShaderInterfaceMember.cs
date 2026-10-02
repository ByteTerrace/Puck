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
/// buffer; <see langword="null"/> for a native record buffer, a raw buffer or a <see cref="ShaderInterfaceMemberKind.Sampler"/>.</param>
/// <param name="Length">The element count of an <see cref="ShaderInterfaceMemberKind.Array"/> or of a block array (a
/// <see cref="ShaderInterfaceMemberKind.Value"/> of four-component vectors), at least one, or the descriptor count of an
/// arrayed <see cref="ShaderInterfaceMemberKind.SampledImage"/> or <see cref="ShaderInterfaceMemberKind.Sampler"/>, which
/// a pass indexes; <see langword="null"/> for a single value, image or sampler and for every other kind.</param>
/// <param name="Format">The texel format of a <see cref="ShaderInterfaceMemberKind.StorageImage"/>;
/// <see langword="null"/> for every other kind.</param>
/// <param name="Structure">A structured buffer's native record description, instead of a primitive element type.</param>
public sealed record ShaderInterfaceMember(
    string Name,
    ShaderInterfaceGroup Group,
    ShaderInterfaceMemberKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ShaderValueType? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Length = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GpuPixelFormat? Format = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ShaderInterfaceStructure? Structure = null
) {
    /// <summary>Creates a scalar or vector member of its group's constant block, or a fixed-length array of
    /// four-component vectors there, whose element <c>i</c> lies <c>16 i</c> bytes past its first on both
    /// backends.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="type">The value type, a four-component vector for an array.</param>
    /// <param name="length">The element count of a block array, at least one, or <see langword="null"/> for a single
    /// value.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember Value(string name, ShaderInterfaceGroup group, ShaderValueType type, uint? length = null) =>
        new(
            Group: group,
            Kind: ShaderInterfaceMemberKind.Value,
            Length: length,
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
    /// <summary>Creates a sampled image member: one image, or an array of images a pass indexes, which takes
    /// <paramref name="length"/> consecutive bindings' worth of registers.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="type">The texel type a sample returns.</param>
    /// <param name="length">The image count of an arrayed member, at least one, or <see langword="null"/> for a single
    /// image.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember SampledImage(string name, ShaderInterfaceGroup group, ShaderValueType type, uint? length = null) =>
        new(
            Group: group,
            Kind: ShaderInterfaceMemberKind.SampledImage,
            Length: length,
            Name: name,
            Type: type
        );
    /// <summary>Creates a sampler member: one sampler, or an array of samplers a pass indexes.</summary>
    /// <param name="name">The member's name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="length">The sampler count of an arrayed member, at least one, or <see langword="null"/> for a single
    /// sampler.</param>
    /// <returns>The member.</returns>
    public static ShaderInterfaceMember Sampler(string name, ShaderInterfaceGroup group, uint? length = null) =>
        new(
            Group: group,
            Kind: ShaderInterfaceMemberKind.Sampler,
            Length: length,
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
    /// <summary>Creates a read-only buffer of native records described by the existing interface generator.</summary>
    /// <param name="name">The shader member name.</param>
    /// <param name="group">The member's frequency group.</param>
    /// <param name="structure">The native element layout.</param>
    /// <returns>The buffer member.</returns>
    public static ShaderInterfaceMember ReadOnlyBuffer(string name, ShaderInterfaceGroup group, ShaderInterfaceStructure structure) =>
        new(Name: name, Group: group, Kind: ShaderInterfaceMemberKind.ReadOnlyBuffer, Structure: structure);
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

    /// <summary>Gets the number of descriptors the member binds: an arrayed sampled image's or sampler's
    /// <see cref="Length"/>, and one for every other binding.</summary>
    [JsonIgnore]
    public uint DescriptorCount => ((Kind is ShaderInterfaceMemberKind.SampledImage or ShaderInterfaceMemberKind.Sampler)
        ? (Length ?? 1u)
        : 1u);
    /// <summary>Gets a value indicating whether the member lives in its group's constant block: a value does, and every
    /// other kind, an array included, is a binding of its own.</summary>
    [JsonIgnore]
    public bool IsBlockMember => (Kind == ShaderInterfaceMemberKind.Value);
    /// <summary>Gets the reflected resource name, including a record-layout identity for structured records.</summary>
    [JsonIgnore]
    public string ResourceName => ((Structure is { } structure) ? $"{Name}Layout{structure.Hash.Hex}" : Name);
}
