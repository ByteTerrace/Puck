namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a sampler to create: its addressing, filtering, mipmapping, anisotropy, comparison, and
/// level-of-detail parameters.
/// </summary>
/// <param name="AddressModeU">The addressing mode on the U axis, as a <c>VkSamplerAddressMode</c> value.</param>
/// <param name="AddressModeV">The addressing mode on the V axis, as a <c>VkSamplerAddressMode</c> value.</param>
/// <param name="AddressModeW">The addressing mode on the W axis, as a <c>VkSamplerAddressMode</c> value.</param>
/// <param name="AnisotropyEnable">A <c>VkBool32</c>; <c>VK_TRUE</c> enables anisotropic filtering.</param>
/// <param name="BorderColor">The predefined border color used with clamp-to-border, as a <c>VkBorderColor</c> value.</param>
/// <param name="CompareEnable">A <c>VkBool32</c>; <c>VK_TRUE</c> enables comparison against a reference during lookups.</param>
/// <param name="CompareOp">The comparison operator applied when comparison is enabled, as a <c>VkCompareOp</c> value.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="Flags">A bitmask of <c>VkSamplerCreateFlagBits</c> specifying additional parameters.</param>
/// <param name="MagFilter">The magnification filter, as a <c>VkFilter</c> value.</param>
/// <param name="MaxAnisotropy">The anisotropy clamp used when anisotropic filtering is enabled.</param>
/// <param name="MaxLod">The upper clamp of the computed level-of-detail.</param>
/// <param name="MinFilter">The minification filter, as a <c>VkFilter</c> value.</param>
/// <param name="MinLod">The lower clamp of the computed level-of-detail.</param>
/// <param name="MipLodBias">The bias added to the computed level-of-detail.</param>
/// <param name="MipmapMode">The mipmap filtering mode, as a <c>VkSamplerMipmapMode</c> value.</param>
/// <param name="UnnormalizedCoordinates">A <c>VkBool32</c>; <c>VK_TRUE</c> selects unnormalized (texel-space) coordinates.</param>
public readonly record struct VulkanSamplerCreateRequest(
    uint AddressModeU,
    uint AddressModeV,
    uint AddressModeW,
    uint AnisotropyEnable,
    uint BorderColor,
    uint CompareEnable,
    uint CompareOp,
    nint DeviceHandle,
    uint Flags,
    uint MagFilter,
    float MaxAnisotropy,
    float MaxLod,
    uint MinFilter,
    float MinLod,
    float MipLodBias,
    uint MipmapMode,
    uint UnnormalizedCoordinates
);
