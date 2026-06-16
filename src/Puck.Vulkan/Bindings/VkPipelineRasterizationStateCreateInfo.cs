using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the rasterization state of a graphics pipeline: polygon fill mode, face culling, depth
/// clamping and bias, and line width.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineRasterizationStateCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout,
/// C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineRasterizationStateCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> clamps fragment depth to the viewport's depth range instead of clipping.</summary>
    public uint DepthClampEnable;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> discards primitives immediately before rasterization.</summary>
    public uint RasterizerDiscardEnable;
    /// <summary>The triangle rendering mode (fill, line, or point), as a <c>VkPolygonMode</c> value.</summary>
    public uint PolygonMode;
    /// <summary>A bitmask of <c>VkCullModeFlagBits</c> selecting the triangle facing direction(s) culled.</summary>
    public uint CullMode;
    /// <summary>The front-facing triangle orientation used for culling, as a <c>VkFrontFace</c> value.</summary>
    public uint FrontFace;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> enables biasing of fragment depth values.</summary>
    public uint DepthBiasEnable;
    /// <summary>A scalar factor controlling the constant depth value added to each fragment.</summary>
    public float DepthBiasConstantFactor;
    /// <summary>The maximum (or minimum) depth bias of a fragment.</summary>
    public float DepthBiasClamp;
    /// <summary>A scalar factor applied to a fragment's slope in depth bias calculations.</summary>
    public float DepthBiasSlopeFactor;
    /// <summary>The width, in pixels, of rasterized line segments.</summary>
    public float LineWidth;
}
