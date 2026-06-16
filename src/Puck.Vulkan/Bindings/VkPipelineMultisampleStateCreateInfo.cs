using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the multisample state of a graphics pipeline: the sample count, sample shading, the coverage
/// sample mask, and alpha-to-coverage / alpha-to-one behavior.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineMultisampleStateCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineMultisampleStateCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The number of samples used in rasterization, as a <c>VkSampleCountFlagBits</c> value.</summary>
    public uint RasterizationSamples;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> enables per-sample shading.</summary>
    public uint SampleShadingEnable;
    /// <summary>The minimum fraction of samples shaded when <see cref="SampleShadingEnable"/> is set.</summary>
    public float MinSampleShading;
    /// <summary>A pointer to an array of <c>VkSampleMask</c> values that is ANDed with the coverage mask, or <see langword="null"/>.</summary>
    public nint PSampleMask;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> derives a temporary coverage value from the alpha component of the fragment's first color output.</summary>
    public uint AlphaToCoverageEnable;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> replaces the alpha component of the fragment's first color output with one.</summary>
    public uint AlphaToOneEnable;
}
