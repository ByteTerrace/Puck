using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>What one pass interface member is. Values live in their group's constant block; arrays, images, buffers and
/// samplers are bindings of their own.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderInterfaceMemberKind>))]
public enum ShaderInterfaceMemberKind {
    /// <summary>A scalar or vector in the group's constant block.</summary>
    Value,
    /// <summary>A fixed-length array of scalars: a read-only <c>StructuredBuffer&lt;T&gt;</c> of its element type, read
    /// through a generated accessor that reads zero past its length.</summary>
    Array,
    /// <summary>A two-dimensional image read through a sampler.</summary>
    SampledImage,
    /// <summary>A two-dimensional image loaded and stored by coordinate.</summary>
    StorageImage,
    /// <summary>A buffer a pass reads: a <c>StructuredBuffer&lt;T&gt;</c> of the member's element type, or, with no
    /// element type, a raw buffer read by byte address (<c>ByteAddressBuffer</c>).</summary>
    ReadOnlyBuffer,
    /// <summary>A buffer a pass reads and writes: a <c>RWStructuredBuffer&lt;T&gt;</c> of the member's element type, or,
    /// with no element type, a raw buffer read and written by byte address (<c>RWByteAddressBuffer</c>).</summary>
    ReadWriteBuffer,
    /// <summary>A sampler.</summary>
    Sampler,
}
