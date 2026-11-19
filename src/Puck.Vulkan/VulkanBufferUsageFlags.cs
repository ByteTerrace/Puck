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
    /// <summary>The <c>VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT</c> value.</summary>
    public const uint UniformBuffer = 0x00000010;
    /// <summary>The <c>VK_BUFFER_USAGE_VERTEX_BUFFER_BIT</c> value.</summary>
    public const uint VertexBuffer = 0x00000080;
    /// <summary><c>VK_BUFFER_USAGE_SHADER_DEVICE_ADDRESS_BIT</c> (vulkan_core.h, SDK 1.4.363): the buffer has a device
    /// address, on a device created with <c>bufferDeviceAddress</c>.</summary>
    public const uint ShaderDeviceAddress = 0x00020000;
    /// <summary><c>VK_BUFFER_USAGE_ACCELERATION_STRUCTURE_BUILD_INPUT_READ_ONLY_BIT_KHR</c>: a build reads the buffer's
    /// vertices, indices or instance records.</summary>
    public const uint AccelerationStructureBuildInput = 0x00080000;
    /// <summary><c>VK_BUFFER_USAGE_ACCELERATION_STRUCTURE_STORAGE_BIT_KHR</c>: an acceleration structure lives in the
    /// buffer.</summary>
    public const uint AccelerationStructureStorage = 0x00100000;
}
