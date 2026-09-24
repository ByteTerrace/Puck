using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>The closed set of binding kinds every pass kind shares. A pass interface generates constant buffers,
/// sampled images, storage images and samplers; the bytecode readers report every kind they meet, so a binding the
/// interface did not declare is named rather than dropped.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderBindingKind>))]
public enum ShaderBindingKind {
    /// <summary>A constant buffer: a Vulkan uniform buffer, a Direct3D 12 CBV.</summary>
    ConstantBuffer,
    /// <summary>A read-only buffer: a Vulkan storage buffer written by no stage, a Direct3D 12 buffer SRV.</summary>
    ReadOnlyBuffer,
    /// <summary>A read-write buffer: a Vulkan storage buffer, a Direct3D 12 buffer UAV.</summary>
    ReadWriteBuffer,
    /// <summary>A sampled image: a Vulkan sampled image, a Direct3D 12 texture SRV.</summary>
    SampledImage,
    /// <summary>A storage image: a Vulkan storage image, a Direct3D 12 texture UAV.</summary>
    StorageImage,
    /// <summary>A sampler: a Vulkan sampler, a Direct3D 12 sampler.</summary>
    Sampler,
    /// <summary>A constant block delivered with the commands rather than bound: Vulkan push constants, Direct3D 12 root
    /// constants. It has no descriptor of its own, so the neutral shape places it at binding 0 of its group's set, and a
    /// DXIL container reflects it as the constant buffer at register <c>b0</c>, space 0.</summary>
    PushConstants,
}
