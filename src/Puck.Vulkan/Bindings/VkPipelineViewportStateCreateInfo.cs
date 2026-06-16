using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the viewport state of a graphics pipeline: the viewports and scissor rectangles used during
/// rasterization. When either is set as dynamic pipeline state, the corresponding pointer is ignored.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineViewportStateCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineViewportStateCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The number of viewports used by the pipeline.</summary>
    public uint ViewportCount;
    /// <summary>A pointer to an array of <c>VkViewport</c> structures; ignored when the viewport is dynamic state.</summary>
    public nint PViewports;
    /// <summary>The number of scissor rectangles used by the pipeline; must equal <see cref="ViewportCount"/>.</summary>
    public uint ScissorCount;
    /// <summary>A pointer to an array of <c>VkRect2D</c> scissor rectangles; ignored when the scissor is dynamic state.</summary>
    public nint PScissors;
}
