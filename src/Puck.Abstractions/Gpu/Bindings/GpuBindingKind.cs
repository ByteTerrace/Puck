using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Abstractions.Gpu;

/// <summary>The closed set of descriptor binding kinds, shared by graphics and compute. Whether a buffer is read or
/// written is part of its kind, so this set is the one statement of buffer access. Push constants are not a kind: a
/// pipeline pushes only an index, never a block.</summary>
[JsonConverter(typeof(StrictEnumConverter<GpuBindingKind>))]
public enum GpuBindingKind {
    /// <summary>A constant buffer: a Vulkan uniform buffer, a Direct3D 12 CBV.</summary>
    ConstantBuffer,
    /// <summary>A read-only buffer: a Vulkan storage buffer written by no stage, a Direct3D 12 buffer SRV
    /// (<c>StructuredBuffer</c> or <c>ByteAddressBuffer</c>).</summary>
    ReadOnlyBuffer,
    /// <summary>A read-write buffer: a Vulkan storage buffer, a Direct3D 12 buffer UAV (<c>RWStructuredBuffer</c> or
    /// <c>RWByteAddressBuffer</c>).</summary>
    ReadWriteBuffer,
    /// <summary>A sampled image: a Vulkan sampled image, a Direct3D 12 texture SRV.</summary>
    SampledImage,
    /// <summary>A storage image: a Vulkan storage image, a Direct3D 12 texture UAV.</summary>
    StorageImage,
    /// <summary>A sampler: a Vulkan sampler, a Direct3D 12 sampler.</summary>
    Sampler,
}
