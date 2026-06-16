using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the color blending state for a single color attachment: whether blending is enabled, the
/// source and destination factors and operations for color and alpha, and the per-component write mask.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineColorBlendAttachmentState (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineColorBlendAttachmentState {
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> enables blending for the attachment, otherwise the source color passes through unmodified.</summary>
    public uint BlendEnable;
    /// <summary>The blend factor applied to the source color, as a <c>VkBlendFactor</c> value.</summary>
    public uint SrcColorBlendFactor;
    /// <summary>The blend factor applied to the destination color, as a <c>VkBlendFactor</c> value.</summary>
    public uint DstColorBlendFactor;
    /// <summary>The blend operation combining the source and destination color, as a <c>VkBlendOp</c> value.</summary>
    public uint ColorBlendOp;
    /// <summary>The blend factor applied to the source alpha, as a <c>VkBlendFactor</c> value.</summary>
    public uint SrcAlphaBlendFactor;
    /// <summary>The blend factor applied to the destination alpha, as a <c>VkBlendFactor</c> value.</summary>
    public uint DstAlphaBlendFactor;
    /// <summary>The blend operation combining the source and destination alpha, as a <c>VkBlendOp</c> value.</summary>
    public uint AlphaBlendOp;
    /// <summary>A bitmask of <c>VkColorComponentFlagBits</c> selecting which color components are written to the attachment.</summary>
    public uint ColorWriteMask;

    /// <summary>
    /// Initializes a new instance, setting only <see cref="BlendEnable"/> and <see cref="ColorWriteMask"/>
    /// and leaving the blend factors and operations zeroed.
    /// </summary>
    /// <param name="blendEnable">The value for <see cref="BlendEnable"/>.</param>
    /// <param name="colorWriteMask">The value for <see cref="ColorWriteMask"/>.</param>
    public VkPipelineColorBlendAttachmentState(uint blendEnable, uint colorWriteMask) {
        BlendEnable = blendEnable;
        ColorWriteMask = colorWriteMask;
    }
}
