using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a single binding within a descriptor set layout: its binding number, descriptor type and
/// count, the shader stages that access it, and any immutable samplers.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDescriptorSetLayoutBinding (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDescriptorSetLayoutBinding {
    /// <summary>The binding number of this entry, matching the binding decoration in the shader.</summary>
    public uint Binding;
    /// <summary>The type of descriptors bound to this binding, as a <c>VkDescriptorType</c> value.</summary>
    public uint DescriptorType;
    /// <summary>The number of descriptors in this binding (the array size, or zero to declare an unused binding).</summary>
    public uint DescriptorCount;
    /// <summary>A bitmask of <c>VkShaderStageFlagBits</c> identifying the shader stages that can access this binding.</summary>
    public uint StageFlags;
    /// <summary>A pointer to an array of immutable <c>VkSampler</c> handles for sampler bindings, or <see langword="null"/>.</summary>
    public nint PImmutableSamplers;
}
