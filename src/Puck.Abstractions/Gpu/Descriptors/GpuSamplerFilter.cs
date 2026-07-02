namespace Puck.Abstractions.Gpu;

/// <summary>
/// The minification/magnification filter of a sampler. On Vulkan it is the filter of the created
/// <c>VkSampler</c>; on Direct3D 12, where samplers are static in the root signature, it is the filter baked into
/// the compute pipeline's static sampler at creation. A sampler is otherwise CLAMP-addressed.
/// </summary>
public enum GpuSamplerFilter : uint {
    /// <summary>Linear (smooth) filtering — bilinear magnification, the fit-to-rect default.</summary>
    Linear = 0,
    /// <summary>Nearest (point) filtering — blocky magnification, the retro/pixelation choice.</summary>
    Nearest = 1,
}
