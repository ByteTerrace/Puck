using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// What the driver reports about how it created a pipeline or one of its stages: whether the report is valid and
/// whether the application's pipeline cache answered the creation.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineCreationFeedback (vulkan_core.h, SDK 1.4, core in Vulkan 1.3): byte-identical layout,
/// C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineCreationFeedback {
    /// <summary><c>VK_PIPELINE_CREATION_FEEDBACK_VALID_BIT</c>: the driver filled this report.</summary>
    public const uint ValidBit = 0x1u;
    /// <summary><c>VK_PIPELINE_CREATION_FEEDBACK_APPLICATION_PIPELINE_CACHE_HIT_BIT</c>: the application's pipeline
    /// cache answered the creation without compiling.</summary>
    public const uint ApplicationPipelineCacheHitBit = 0x2u;

    /// <summary>A bitmask of <c>VkPipelineCreationFeedbackFlagBits</c>.</summary>
    public uint Flags;
    /// <summary>The creation's duration in nanoseconds, as the driver measured it.</summary>
    public ulong Duration;

    /// <summary>Gets whether the report is valid and says the application's pipeline cache answered the creation.</summary>
    public readonly bool IsCacheHit =>
        ((Flags & (ValidBit | ApplicationPipelineCacheHitBit)) == (ValidBit | ApplicationPipelineCacheHitBit));
}
/// <summary>
/// Asks the driver for <see cref="VkPipelineCreationFeedback"/> about a pipeline creation; chained from the create
/// info's <c>pNext</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineCreationFeedbackCreateInfo (vulkan_core.h, SDK 1.4, core in Vulkan 1.3): byte-identical
/// layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineCreationFeedbackCreateInfo {
    /// <summary><c>VK_STRUCTURE_TYPE_PIPELINE_CREATION_FEEDBACK_CREATE_INFO</c>.</summary>
    public const uint StructureType = 1000192000u;

    /// <summary>The type of this structure (<see cref="StructureType"/>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A pointer to the whole pipeline's <see cref="VkPipelineCreationFeedback"/>.</summary>
    public nint PPipelineCreationFeedback;
    /// <summary>The number of per-stage reports at <see cref="PPipelineStageCreationFeedbacks"/>: the pipeline's stage
    /// count.</summary>
    public uint PipelineStageCreationFeedbackCount;
    /// <summary>A pointer to one <see cref="VkPipelineCreationFeedback"/> per shader stage.</summary>
    public nint PPipelineStageCreationFeedbacks;
}
