using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a single executable produced when a pipeline is compiled: the stages it covers, a name and
/// description, and its subgroup size.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineExecutablePropertiesKHR (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPipelineExecutablePropertiesKhr {
    private const int MaxDescriptionSize = 256;

    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_EXECUTABLE_PROPERTIES_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkShaderStageFlagBits</c> identifying the shader stages that contributed to the executable.</summary>
    public uint Stages;
    /// <summary>A short human-readable name for the executable, as a null-terminated UTF-8 string in a fixed 256-byte buffer.</summary>
    public fixed byte Name[MaxDescriptionSize];
    /// <summary>A human-readable description of the executable, as a null-terminated UTF-8 string in a fixed 256-byte buffer.</summary>
    public fixed byte Description[MaxDescriptionSize];
    /// <summary>The subgroup size the executable runs with.</summary>
    public uint SubgroupSize;
}
