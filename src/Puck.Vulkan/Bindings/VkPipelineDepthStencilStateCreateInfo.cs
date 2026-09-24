using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the stencil operations and masks of one face; Puck never enables stencil testing, so every field is zero.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkStencilOpState (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkStencilOpState {
    /// <summary>The <c>VkStencilOp</c> applied when the stencil test fails.</summary>
    public uint FailOp;
    /// <summary>The <c>VkStencilOp</c> applied when both tests pass.</summary>
    public uint PassOp;
    /// <summary>The <c>VkStencilOp</c> applied when the stencil test passes and the depth test fails.</summary>
    public uint DepthFailOp;
    /// <summary>The stencil test's <c>VkCompareOp</c>.</summary>
    public uint CompareOp;
    /// <summary>The bits of the stencil value the test reads.</summary>
    public uint CompareMask;
    /// <summary>The bits of the stencil value a write updates.</summary>
    public uint WriteMask;
    /// <summary>The reference value the test compares against.</summary>
    public uint Reference;
}
/// <summary>
/// Describes the depth and stencil state of a graphics pipeline: whether the depth test runs, whether a passing fragment
/// writes its depth, and the comparison it passes by.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineDepthStencilStateCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineDepthStencilStateCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value
    /// (<c>VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> runs the depth test.</summary>
    public uint DepthTestEnable;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> writes a passing fragment's depth.</summary>
    public uint DepthWriteEnable;
    /// <summary>The depth test's <c>VkCompareOp</c>.</summary>
    public uint DepthCompareOp;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> enables the depth bounds test.</summary>
    public uint DepthBoundsTestEnable;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> enables the stencil test.</summary>
    public uint StencilTestEnable;
    /// <summary>The front face's stencil state.</summary>
    public VkStencilOpState Front;
    /// <summary>The back face's stencil state.</summary>
    public VkStencilOpState Back;
    /// <summary>The lower depth bound.</summary>
    public float MinDepthBounds;
    /// <summary>The upper depth bound.</summary>
    public float MaxDepthBounds;
}
