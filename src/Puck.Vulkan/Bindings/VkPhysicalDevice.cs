namespace Puck.Vulkan.Bindings;

/// <summary>
/// A selected physical device: its native <c>VkPhysicalDevice</c> handle paired with the device type and the
/// queue families chosen for use with it.
/// </summary>
public readonly record struct VkPhysicalDevice {
    /// <summary>Gets the kind of the physical device.</summary>
    public VkPhysicalDeviceType DeviceType { get; }
    /// <summary>Gets the native <c>VkPhysicalDevice</c> handle.</summary>
    public nint Handle { get; }
    /// <summary>Gets the queue families selected for use with this device.</summary>
    public VulkanQueueFamilySelection QueueFamilySelection { get; }

    /// <summary>Initializes a new instance of the <see cref="VkPhysicalDevice"/> struct.</summary>
    /// <param name="handle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="deviceType">The kind of the physical device.</param>
    /// <param name="queueFamilySelection">The queue families selected for use with the device.</param>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is zero.</exception>
    public VkPhysicalDevice(
        nint handle,
        VkPhysicalDeviceType deviceType,
        VulkanQueueFamilySelection queueFamilySelection
    ) {
        if (0 == handle) {
            throw new ArgumentException(
                message: "Vulkan physical-device handle must be non-zero.",
                paramName: nameof(handle)
            );
        }

        Handle = handle;
        DeviceType = deviceType;
        QueueFamilySelection = queueFamilySelection;
    }
}
