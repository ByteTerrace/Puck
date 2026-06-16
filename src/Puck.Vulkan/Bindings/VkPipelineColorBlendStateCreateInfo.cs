using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the color blend state of a graphics pipeline: the optional logical operation, the per-attachment
/// blend states, and the blend constants used by constant-based blend factors.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): the C array blendConstants[4] is expanded to the scalar fields BlendConstants0..3. Layout and
/// size match VkPipelineColorBlendStateCreateInfo exactly.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineColorBlendStateCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> applies the logical operation in <see cref="LogicOp"/> instead of blending.</summary>
    public uint LogicOpEnable;
    /// <summary>The logical operation applied when <see cref="LogicOpEnable"/> is set, as a <c>VkLogicOp</c> value.</summary>
    public uint LogicOp;
    /// <summary>The number of entries in the <see cref="PAttachments"/> array; must match the subpass color attachment count.</summary>
    public uint AttachmentCount;
    /// <summary>A pointer to an array of <c>VkPipelineColorBlendAttachmentState</c> structures, one per color attachment.</summary>
    public nint PAttachments;
    /// <summary>The R component of the blend constant used by constant-based blend factors.</summary>
    public float BlendConstants0;
    /// <summary>The G component of the blend constant used by constant-based blend factors.</summary>
    public float BlendConstants1;
    /// <summary>The B component of the blend constant used by constant-based blend factors.</summary>
    public float BlendConstants2;
    /// <summary>The A component of the blend constant used by constant-based blend factors.</summary>
    public float BlendConstants3;
}
