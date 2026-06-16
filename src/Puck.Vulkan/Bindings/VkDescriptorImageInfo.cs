using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Specifies the image resources bound to an image, sampler, or combined image-sampler descriptor.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDescriptorImageInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDescriptorImageInfo {
    /// <summary>The sampler bound to the descriptor (a <c>VkSampler</c> handle); ignored for descriptor types that do not use a sampler.</summary>
    public nint Sampler;
    /// <summary>The image view bound to the descriptor (a <c>VkImageView</c> handle); ignored for descriptor types that do not use an image view.</summary>
    public nint ImageView;
    /// <summary>The layout the image subresource is in at access time, as a <c>VkImageLayout</c> value.</summary>
    public uint ImageLayout;
}
