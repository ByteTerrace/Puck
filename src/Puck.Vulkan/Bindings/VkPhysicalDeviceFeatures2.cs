using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Retrieves the features a physical device supports, extended through a <c>pNext</c> chain. The base
/// <c>VkPhysicalDeviceFeatures</c> is flattened into <see cref="Features"/>, one <c>VkBool32</c> per feature.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): the nested VkPhysicalDeviceFeatures is flattened to Features[55], the same 55-VkBool32 block
/// the plain VkPhysicalDeviceFeatures path uses (real sizeof 240, tail-padded to 8-byte alignment).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPhysicalDeviceFeatures2 {
    private const int PhysicalDeviceFeatureCount = 55;

    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the chained structure that receives the extended features, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The 55 <c>VkBool32</c> feature flags of the base <c>VkPhysicalDeviceFeatures</c>, in declaration order.</summary>
    public fixed uint Features[PhysicalDeviceFeatureCount];
}
