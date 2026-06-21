namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkAccessFlagBits</c> values used in pipeline barriers and layout transitions. Combine with bitwise OR.
/// </summary>
public static class VulkanAccessFlags {
    /// <summary>The <c>VK_ACCESS_INDIRECT_COMMAND_READ_BIT</c> value.</summary>
    public const uint IndirectCommandRead = 0x00000001;
    /// <summary>The <c>VK_ACCESS_SHADER_READ_BIT</c> value.</summary>
    public const uint ShaderRead = 0x00000020;
    /// <summary>The <c>VK_ACCESS_SHADER_WRITE_BIT</c> value.</summary>
    public const uint ShaderWrite = 0x00000040;
    /// <summary>The <c>VK_ACCESS_COLOR_ATTACHMENT_READ_BIT</c> value.</summary>
    public const uint ColorAttachmentRead = 0x00000080;
    /// <summary>The <c>VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT</c> value.</summary>
    public const uint ColorAttachmentWrite = 0x00000100;
    /// <summary>The <c>VK_ACCESS_TRANSFER_READ_BIT</c> value.</summary>
    public const uint TransferRead = 0x00000800;
    /// <summary>The <c>VK_ACCESS_TRANSFER_WRITE_BIT</c> value.</summary>
    public const uint TransferWrite = 0x00001000;
}
