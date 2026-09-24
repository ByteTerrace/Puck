using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>The descriptor kind of one <see cref="ShaderSetManifestBinding"/>, in the manifest's own coarse,
/// backend-facing vocabulary — the JSON string is lower camelCase (<c>storageBuffer</c>,
/// <c>sampledImage</c>, <c>storageImage</c>).</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderSetManifestBindingKind>))]
public enum ShaderSetManifestBindingKind {
    /// <summary>A storage buffer — a Vulkan storage buffer, or a Direct3D 12 SRV/UAV.</summary>
    [JsonStringEnumMemberName(name: "storageBuffer")]
    StorageBuffer = 0,
    /// <summary>A sampled image — a Vulkan combined-image-sampler, or a Direct3D 12 SRV read through a static
    /// sampler.</summary>
    [JsonStringEnumMemberName(name: "sampledImage")]
    SampledImage = 1,
    /// <summary>A storage image — a Vulkan storage image, or a Direct3D 12 UAV.</summary>
    [JsonStringEnumMemberName(name: "storageImage")]
    StorageImage = 2,
}
