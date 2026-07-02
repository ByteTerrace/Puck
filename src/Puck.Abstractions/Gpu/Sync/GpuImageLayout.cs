namespace Puck.Abstractions.Gpu;

/// <summary>
/// Specifies the neutral layout of a storage image across a compute barrier, mapped per backend (a Vulkan image
/// layout, or a Direct3D 12 resource state).
/// </summary>
public enum GpuImageLayout : uint {
    /// <summary>Undefined contents; the prior layout is discarded.</summary>
    Undefined = 0,
    /// <summary>The general read/write layout for a compute dispatch (Vulkan GENERAL; Direct3D 12 UNORDERED_ACCESS).</summary>
    General = 1,
    /// <summary>The shader-readable layout for sampling (Vulkan SHADER_READ_ONLY_OPTIMAL; Direct3D 12 PIXEL_SHADER_RESOURCE).</summary>
    ShaderReadOnly = 2,
    /// <summary>The cross-backend handoff layout an importer on another backend samples from after opening the shared
    /// handle (Vulkan GENERAL; Direct3D 12 COMMON — the state a shared resource must rest in for a foreign device).</summary>
    External = 3,
}
