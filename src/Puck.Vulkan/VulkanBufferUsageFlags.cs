namespace Puck.Vulkan;

/// <summary>
/// The <c>VkBufferUsageFlagBits</c> values the backend creates buffers with. Combine with bitwise OR.
/// </summary>
public static class VulkanBufferUsageFlags {
    /// <summary>The <c>VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT</c> value: the buffer supplies indirect dispatch or draw
    /// arguments.</summary>
    public const uint IndirectBuffer = 0x00000100;
    /// <summary>The <c>VK_BUFFER_USAGE_INDEX_BUFFER_BIT</c> value.</summary>
    public const uint IndexBuffer = 0x00000040;
    /// <summary>The usage of every storage buffer the backend creates: a shader storage buffer that is also a transfer
    /// source, so a host-coherent one can stage an upload, and a transfer destination.</summary>
    public const uint Storage = StorageBuffer | TransferSource | TransferDestination;
    /// <summary>The <c>VK_BUFFER_USAGE_STORAGE_BUFFER_BIT</c> value.</summary>
    public const uint StorageBuffer = 0x00000020;
    /// <summary>The <c>VK_BUFFER_USAGE_TRANSFER_DST_BIT</c> value.</summary>
    public const uint TransferDestination = 0x00000002;
    /// <summary>The <c>VK_BUFFER_USAGE_TRANSFER_SRC_BIT</c> value.</summary>
    public const uint TransferSource = 0x00000001;
    /// <summary>The <c>VK_BUFFER_USAGE_VERTEX_BUFFER_BIT</c> value.</summary>
    public const uint VertexBuffer = 0x00000080;
}
