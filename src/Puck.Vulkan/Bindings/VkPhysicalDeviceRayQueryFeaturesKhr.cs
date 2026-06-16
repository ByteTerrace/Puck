using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports or requests whether a physical device supports ray query operations in shaders; chained into the
/// feature/device-creation query via <c>pNext</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPhysicalDeviceRayQueryFeaturesKHR (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPhysicalDeviceRayQueryFeaturesKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_QUERY_FEATURES_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A <c>VkBool32</c>; whether ray query (the <c>SPV_KHR_ray_query</c> extension) is supported in shaders.</summary>
    public uint RayQuery;
}
