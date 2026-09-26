using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>What one pass interface member is. Values and arrays live in their group's constant block; images, buffers and
/// samplers are bindings of their own.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderInterfaceMemberKind>))]
public enum ShaderInterfaceMemberKind {
    /// <summary>A scalar or vector in the group's constant block.</summary>
    Value,
    /// <summary>A fixed-length array of scalars or vectors in the group's constant block, read through a generated
    /// accessor that hides how an element is stored.</summary>
    Array,
    /// <summary>A two-dimensional image read through a sampler.</summary>
    SampledImage,
    /// <summary>A two-dimensional image loaded and stored by coordinate.</summary>
    StorageImage,
    /// <summary>A raw buffer read by byte address (<c>ByteAddressBuffer</c>).</summary>
    ReadOnlyBuffer,
    /// <summary>A raw buffer read and written by byte address (<c>RWByteAddressBuffer</c>).</summary>
    ReadWriteBuffer,
    /// <summary>A sampler.</summary>
    Sampler,
}
