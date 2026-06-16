using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Specifies how many descriptors of a given type a descriptor pool should be able to allocate.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDescriptorPoolSize (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDescriptorPoolSize {
    /// <summary>The type of descriptors counted by this entry, as a <c>VkDescriptorType</c> value.</summary>
    public uint Type;
    /// <summary>The number of descriptors of <see cref="Type"/> to reserve in the pool.</summary>
    public uint DescriptorCount;
}
