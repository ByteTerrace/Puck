namespace Puck.Vulkan.Bindings;

/// <summary>
/// Specifies the kind of a physical device, as reported by <c>VkPhysicalDeviceProperties.deviceType</c>.
/// </summary>
public enum VkPhysicalDeviceType {
    /// <summary>The device does not match any other available type.</summary>
    Other = 0,
    /// <summary>The device is typically one embedded in or tightly coupled with the host (an integrated GPU).</summary>
    IntegratedGpu = 1,
    /// <summary>The device is typically a separate processor connected to the host over an interlink (a discrete GPU).</summary>
    DiscreteGpu = 2,
    /// <summary>The device is typically a virtual node in a virtualization environment.</summary>
    VirtualGpu = 3,
    /// <summary>The device is typically running on the same processors as the host (a software/CPU implementation).</summary>
    Cpu = 4
}
