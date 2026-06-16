namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkPipelineStageFlagBits</c> values used in pipeline barriers and layout transitions. Combine with bitwise OR.
/// </summary>
public static class VulkanPipelineStageFlags {
    /// <summary>The <c>VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT</c> value.</summary>
    public const uint TopOfPipe = 0x00000001;
    /// <summary>The <c>VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT</c> value.</summary>
    public const uint FragmentShader = 0x00000080;
    /// <summary>The <c>VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT</c> value.</summary>
    public const uint ColorAttachmentOutput = 0x00000400;
    /// <summary>The <c>VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT</c> value.</summary>
    public const uint ComputeShader = 0x00000800;
    /// <summary>The <c>VK_PIPELINE_STAGE_TRANSFER_BIT</c> value.</summary>
    public const uint Transfer = 0x00001000;
    /// <summary>The <c>VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT</c> value.</summary>
    public const uint BottomOfPipe = 0x00002000;
}
