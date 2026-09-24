namespace Puck.Vulkan;

/// <summary>
/// The device memory a buffer is allocated from, as <see cref="Interfaces.IVulkanBufferApi.Create"/> selects a memory
/// type for it.
/// </summary>
public enum VulkanBufferMemory {
    /// <summary>A type that is both <c>HOST_VISIBLE</c> and <c>HOST_COHERENT</c>, or the creation fails. The host writes
    /// and reads it through a mapping without a flush or an invalidate.</summary>
    HostCoherent,
    /// <summary>A <c>DEVICE_LOCAL</c> type, or the creation fails. The host never maps it.</summary>
    DeviceLocal,
    /// <summary>A <c>DEVICE_LOCAL</c> type when one is permitted, otherwise the first type the buffer's requirements
    /// permit. The host never maps it.</summary>
    PreferDeviceLocal,
}
