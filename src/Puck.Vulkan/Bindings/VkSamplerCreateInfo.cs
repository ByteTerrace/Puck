using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a sampler to be created with <c>vkCreateSampler</c>: its filtering, mipmapping,
/// addressing, anisotropy, comparison, and level-of-detail behavior.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSamplerCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSamplerCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkSamplerCreateFlagBits</c> specifying additional parameters of the sampler.</summary>
    public uint Flags;
    /// <summary>The filter applied when the image is magnified, as a <c>VkFilter</c> value.</summary>
    public uint MagFilter;
    /// <summary>The filter applied when the image is minified, as a <c>VkFilter</c> value.</summary>
    public uint MinFilter;
    /// <summary>The filter applied between mipmap levels, as a <c>VkSamplerMipmapMode</c> value.</summary>
    public uint MipmapMode;
    /// <summary>The addressing mode for texel coordinates outside <c>[0, 1)</c> on the U axis, as a <c>VkSamplerAddressMode</c> value.</summary>
    public uint AddressModeU;
    /// <summary>The addressing mode for texel coordinates outside <c>[0, 1)</c> on the V axis, as a <c>VkSamplerAddressMode</c> value.</summary>
    public uint AddressModeV;
    /// <summary>The addressing mode for texel coordinates outside <c>[0, 1)</c> on the W axis, as a <c>VkSamplerAddressMode</c> value.</summary>
    public uint AddressModeW;
    /// <summary>The bias added to the computed level-of-detail before clamping.</summary>
    public float MipLodBias;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> enables anisotropic filtering.</summary>
    public uint AnisotropyEnable;
    /// <summary>The maximum degree of anisotropy used when <see cref="AnisotropyEnable"/> is set.</summary>
    public float MaxAnisotropy;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> enables comparison against a reference value during lookups.</summary>
    public uint CompareEnable;
    /// <summary>The comparison operator applied when <see cref="CompareEnable"/> is set, as a <c>VkCompareOp</c> value.</summary>
    public uint CompareOp;
    /// <summary>The lower clamp applied to the computed level-of-detail.</summary>
    public float MinLod;
    /// <summary>The upper clamp applied to the computed level-of-detail.</summary>
    public float MaxLod;
    /// <summary>The predefined border color used with clamp-to-border addressing, as a <c>VkBorderColor</c> value.</summary>
    public uint BorderColor;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> selects unnormalized (texel-space) coordinates instead of normalized ones.</summary>
    public uint UnnormalizedCoordinates;
}
