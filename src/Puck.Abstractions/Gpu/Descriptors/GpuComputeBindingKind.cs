namespace Puck.Abstractions.Gpu;

/// <summary>
/// Specifies how a compute descriptor binding is accessed, so each backend can pick the matching descriptor kind
/// (a Vulkan storage image/buffer, or a Direct3D 12 UAV/SRV).
/// </summary>
public enum GpuComputeBindingKind : uint {
    /// <summary>A storage image the shader writes (a Vulkan storage image, or a Direct3D 12 UAV).</summary>
    StorageImage = 1,
    /// <summary>A read-only storage buffer (a Vulkan storage buffer, or a Direct3D 12 SRV).</summary>
    StorageBufferRead = 2,
    /// <summary>A storage buffer the shader writes (a Vulkan storage buffer, or a Direct3D 12 UAV).</summary>
    StorageBufferReadWrite = 3,
    /// <summary>A top-level acceleration structure bound to a ray-query shader (a Vulkan acceleration-structure
    /// descriptor, or a Direct3D 12 raytracing-acceleration-structure SRV). Usable only on devices with inline ray
    /// tracing (Vulkan <c>VK_KHR_ray_query</c> or Direct3D 12 DXR 1.1).</summary>
    AccelerationStructure = 4,
    /// <summary>A read-only sampled image the shader filters through a sampler — the compute-seam analogue of the
    /// graphics texture binding (a Vulkan combined-image-sampler, or a Direct3D 12 SRV read through a static sampler
    /// in the root signature). The sampler's filter is fixed at pipeline creation via the factory's
    /// <c>samplerFilter</c>; the source must be in a shader-readable layout when sampled.</summary>
    SampledImage = 5,
}
