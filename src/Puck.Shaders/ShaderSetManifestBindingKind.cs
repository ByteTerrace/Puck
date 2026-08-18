using System.Text.Json.Serialization;

namespace Puck.Shaders;

/// <summary>The descriptor kind of one <see cref="ShaderSetManifestBinding"/>, in the manifest's own coarse,
/// backend-facing vocabulary — the JSON string is lower camelCase (<c>storageBuffer</c>,
/// <c>sampledImage</c>, <c>storageImage</c>, <c>accelerationStructure</c>), via
/// <see cref="ShaderSetManifestBindingKindJsonConverter"/>.</summary>
[JsonConverter(typeof(ShaderSetManifestBindingKindJsonConverter))]
public enum ShaderSetManifestBindingKind {
    /// <summary>A storage buffer — a Vulkan storage buffer, or a Direct3D 12 SRV/UAV.</summary>
    StorageBuffer = 0,
    /// <summary>A sampled image — a Vulkan combined-image-sampler, or a Direct3D 12 SRV read through a static
    /// sampler.</summary>
    SampledImage = 1,
    /// <summary>A storage image — a Vulkan storage image, or a Direct3D 12 UAV.</summary>
    StorageImage = 2,
    /// <summary>A top-level acceleration structure — a Vulkan acceleration-structure descriptor, or a Direct3D 12
    /// raytracing-acceleration-structure SRV.</summary>
    AccelerationStructure = 3,
}
