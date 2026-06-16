using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native physical device query entry points used to enumerate and inspect the GPUs visible to an
/// instance: their features, extensions, queue families, and surface capabilities.
/// </summary>
public interface IVulkanPhysicalDeviceApi {
    /// <summary>Enumerates the physical devices visible to an instance.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <returns>The native <c>VkPhysicalDevice</c> handles of the available devices.</returns>
    IReadOnlyList<nint> EnumeratePhysicalDevices(nint instanceHandle);
    /// <summary>Gets the base feature support of a physical device.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The boolean feature flags of <c>VkPhysicalDeviceFeatures</c>, in declaration order.</returns>
    IReadOnlyList<bool> GetFeatureSupport(nint instanceHandle, nint physicalDeviceHandle);
    /// <summary>Gets the kind of a physical device.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The device type.</returns>
    VkPhysicalDeviceType GetPhysicalDeviceType(nint instanceHandle, nint physicalDeviceHandle);
    /// <summary>Gets a physical device's adapter LUID — the identifier a Direct3D 12 device must be created on to share GPU resources with it.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The packed adapter LUID (<c>HighPart &lt;&lt; 32 | LowPart</c>), or zero if the device reports no valid LUID.</returns>
    long GetDeviceLuid(nint instanceHandle, nint physicalDeviceHandle);
    /// <summary>Determines whether a physical device supports a given device extension.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="extensionName">The name of the extension to test for.</param>
    /// <returns><see langword="true"/> if the extension is supported; otherwise, <see langword="false"/>.</returns>
    bool HasDeviceExtension(nint instanceHandle, nint physicalDeviceHandle, string extensionName);
    /// <summary>Determines whether a physical device reports support for the feature carried by a chained feature structure.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="structureType">The <c>VkStructureType</c> of the feature structure to probe.</param>
    /// <returns><see langword="true"/> if the feature is supported; otherwise, <see langword="false"/>.</returns>
    bool IsExtensionFeatureSupported(nint instanceHandle, nint physicalDeviceHandle, uint structureType);
    /// <summary>Gets the presentation modes a physical device supports for a surface.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>The supported present modes, as <c>VkPresentModeKHR</c> values.</returns>
    IReadOnlyList<uint> GetPresentModes(nint instanceHandle, nint physicalDeviceHandle, nint surfaceHandle);
    /// <summary>Gets the queue families of a physical device.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>A condensed description of each queue family.</returns>
    IReadOnlyList<VkQueueFamilyInfo> GetQueueFamilies(nint instanceHandle, nint physicalDeviceHandle);
    /// <summary>Gets the capabilities of a surface on a physical device.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>The surface capabilities relevant to swapchain creation.</returns>
    VulkanSurfaceCapabilities GetSurfaceCapabilities(nint instanceHandle, nint physicalDeviceHandle, nint surfaceHandle);
    /// <summary>Gets the format/color-space pairs a physical device supports for a surface.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>The supported surface formats.</returns>
    IReadOnlyList<VulkanSurfaceFormat> GetSurfaceFormats(nint instanceHandle, nint physicalDeviceHandle, nint surfaceHandle);
    /// <summary>Determines whether a queue family of a physical device can present to a surface.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="queueFamilyIndex">The index of the queue family to test.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns><see langword="true"/> if the family supports presentation to the surface; otherwise, <see langword="false"/>.</returns>
    bool GetSurfaceSupport(nint instanceHandle, nint physicalDeviceHandle, uint queueFamilyIndex, nint surfaceHandle);
    /// <summary>Gets the timestamp capabilities of a physical device's graphics queue family.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="graphicsQueueFamilyIndex">The index of the graphics queue family.</param>
    /// <returns>The timestamp period and valid-bit count usable for GPU timing.</returns>
    VulkanTimestampCapabilities GetTimestampCapabilities(nint instanceHandle, nint physicalDeviceHandle, uint graphicsQueueFamilyIndex);
}
