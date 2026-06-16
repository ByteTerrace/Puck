using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a logical device to be created with <c>vkCreateDevice</c>, including the queues,
/// extensions, and features to enable.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDeviceCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDeviceCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>. Feature structures are chained here.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The number of entries in the <see cref="PQueueCreateInfos"/> array.</summary>
    public uint QueueCreateInfoCount;
    /// <summary>A pointer to an array of <c>VkDeviceQueueCreateInfo</c> structures describing the queues to create.</summary>
    public nint PQueueCreateInfos;
    /// <summary>Deprecated and ignored. Device layers were removed from Vulkan.</summary>
    public uint EnabledLayerCount;
    /// <summary>Deprecated and ignored. Device layers were removed from Vulkan.</summary>
    public nint PpEnabledLayerNames;
    /// <summary>The number of device extensions to enable.</summary>
    public uint EnabledExtensionCount;
    /// <summary>A pointer to an array of null-terminated UTF-8 strings naming the device extensions to enable.</summary>
    public nint PpEnabledExtensionNames;
    /// <summary>A pointer to a <c>VkPhysicalDeviceFeatures</c> structure listing the features to enable, or <see langword="null"/>.</summary>
    public nint PEnabledFeatures;
}
