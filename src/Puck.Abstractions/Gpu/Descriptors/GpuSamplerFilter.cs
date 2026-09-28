using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The minification and magnification filter of a sampler, and the filter a world document's screen row samples its
/// source with. On Vulkan it is the filter of the created <c>VkSampler</c>; on Direct3D 12 it is the filter of the
/// sampler descriptor written into a group's sampler table, or of a pipeline's static sampler. A sampler is otherwise
/// clamp-addressed. Its value is the index of its sampler in the SDF engine's sampler array (<c>samplers</c> in the
/// <c>sdf-world</c> interface).
/// </summary>
[JsonConverter(typeof(StrictEnumConverter<GpuSamplerFilter>))]
public enum GpuSamplerFilter : uint {
    /// <summary>Nearest (point) filtering: blocky magnification, so each source pixel stays crisp.</summary>
    Nearest = 0,
    /// <summary>Linear (bilinear) filtering: smooth magnification between source pixels.</summary>
    Linear = 1,
}
