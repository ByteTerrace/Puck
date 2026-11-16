using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native physical device query entry points used to enumerate and inspect the GPUs visible to an
/// instance: their features, extensions, queue families, and surface capabilities.
/// </summary>
public interface IVulkanPhysicalDeviceApi {
    /// <summary>Enumerates the physical devices visible to an instance.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <returns>The native <c>VkPhysicalDevice</c> handles of the available devices.</returns>
    IReadOnlyList<nint> EnumeratePhysicalDevices(VulkanInstanceCommands instance);
    /// <summary>Gets the base feature support of a physical device.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The boolean feature flags of <c>VkPhysicalDeviceFeatures</c>, in declaration order.</returns>
    IReadOnlyList<bool> GetFeatureSupport(VulkanInstanceCommands instance, nint physicalDeviceHandle);
    /// <summary>Gets the kind of a physical device.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The device type.</returns>
    VkPhysicalDeviceType GetPhysicalDeviceType(VulkanInstanceCommands instance, nint physicalDeviceHandle);
    /// <summary>Gets the Vulkan API version a physical device supports (the <c>apiVersion</c> field of <c>VkPhysicalDeviceProperties</c>).</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The packed <c>VK_MAKE_API_VERSION</c> value the device reports; a shader module built for a higher version than this is rejected at <c>vkCreateShaderModule</c>.</returns>
    uint GetDeviceApiVersion(VulkanInstanceCommands instance, nint physicalDeviceHandle);
    /// <summary>Gets the human-readable name of a physical device (the <c>deviceName</c> field of <c>VkPhysicalDeviceProperties</c>).</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The device name, for diagnostics.</returns>
    string GetDeviceName(VulkanInstanceCommands instance, nint physicalDeviceHandle);
    /// <summary>Gets what a physical device is: its name, PCI vendor and device identifiers, driver version, API
    /// version, pipeline-cache UUID, and, when the device reports driver properties (Vulkan 1.2 or
    /// <c>VK_KHR_driver_properties</c>), its driver's name, identifier, display version, and conformance version.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The device's identity, recorded for diagnostics and naming its pipeline-cache file, never branched
    /// on.</returns>
    Puck.Abstractions.Gpu.GpuDeviceIdentity GetDeviceIdentity(VulkanInstanceCommands instance, nint physicalDeviceHandle);
    /// <summary>Gets what a physical device's memory is, from its <c>deviceType</c> and
    /// <c>vkGetPhysicalDeviceMemoryProperties</c>'s memory types and heaps.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The device's memory profile, which residency selection branches on.</returns>
    Puck.Abstractions.Gpu.GpuMemoryProfile GetMemoryProfile(VulkanInstanceCommands instance, nint physicalDeviceHandle);
    /// <summary>Gets a physical device's memory segments, one per heap of <c>vkGetPhysicalDeviceMemoryProperties</c>
    /// with the pool it lies in (<see cref="Puck.Abstractions.Gpu.GpuMemorySegments.FromVulkan"/>), and each memory
    /// type's heap.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="profile">The device's memory profile (<see cref="GetMemoryProfile"/>).</param>
    /// <returns>The device's segments.</returns>
    Puck.Abstractions.Gpu.GpuMemorySegments GetMemorySegments(VulkanInstanceCommands instance, nint physicalDeviceHandle, Puck.Abstractions.Gpu.GpuMemoryProfile profile);
    /// <summary>Reads a physical device's budget and usage per heap: <c>vkGetPhysicalDeviceMemoryProperties2</c> with
    /// <c>VkPhysicalDeviceMemoryBudgetPropertiesEXT</c> chained when the device was created with
    /// <c>VK_EXT_memory_budget</c>; otherwise each heap's size as its budget and no usage, an estimate.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="segments">The device's segments (<see cref="GetMemorySegments"/>).</param>
    /// <param name="budgetEnabled">Whether the device was created with <c>VK_EXT_memory_budget</c>.</param>
    /// <param name="estimated">Whether the figures are estimates: the extension is not enabled, or the instance has no
    /// <c>vkGetPhysicalDeviceMemoryProperties2</c>.</param>
    /// <returns>One reading per segment, in the segments' order.</returns>
    Puck.Abstractions.Gpu.GpuSegmentReading[] ReadMemoryBudget(VulkanInstanceCommands instance, nint physicalDeviceHandle, Puck.Abstractions.Gpu.GpuMemorySegments segments, bool budgetEnabled, out bool estimated);
    /// <summary>Gets what a physical device can bind, from <c>vkGetPhysicalDeviceProperties</c>'s limits:
    /// <c>maxBoundDescriptorSets</c>, <c>maxPushConstantsSize</c>, the <c>maxPerStageDescriptor*</c> counts and
    /// <c>maxPerStageResources</c>.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The device's capabilities, recorded beside counted work and never branched on.</returns>
    Puck.Abstractions.Gpu.GpuDeviceCapabilities GetDeviceCapabilities(VulkanInstanceCommands instance, nint physicalDeviceHandle);
    /// <summary>Gets a physical device's adapter LUID — the identifier a Direct3D 12 device must be created on to share GPU resources with it.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The packed adapter LUID (<c>HighPart &lt;&lt; 32 | LowPart</c>), or zero if the device reports no valid LUID.</returns>
    long GetDeviceLuid(VulkanInstanceCommands instance, nint physicalDeviceHandle);
    /// <summary>Determines whether a physical device supports a given device extension.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="extensionName">The name of the extension to test for.</param>
    /// <returns><see langword="true"/> if the extension is supported; otherwise, <see langword="false"/>.</returns>
    bool HasDeviceExtension(VulkanInstanceCommands instance, nint physicalDeviceHandle, string extensionName);
    /// <summary>Determines whether a physical device reports support for the feature carried by a chained feature structure.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="structureType">The <c>VkStructureType</c> of the feature structure to probe.</param>
    /// <returns><see langword="true"/> if the feature is supported; otherwise, <see langword="false"/>.</returns>
    bool IsExtensionFeatureSupported(VulkanInstanceCommands instance, nint physicalDeviceHandle, uint structureType);
    /// <summary>Gets the presentation modes a physical device supports for a surface.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>The supported present modes, as <c>VkPresentModeKHR</c> values.</returns>
    IReadOnlyList<uint> GetPresentModes(VulkanInstanceCommands instance, nint physicalDeviceHandle, nint surfaceHandle);
    /// <summary>Gets the queue families of a physical device.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>A condensed description of each queue family.</returns>
    IReadOnlyList<VkQueueFamilyInfo> GetQueueFamilies(VulkanInstanceCommands instance, nint physicalDeviceHandle);
    /// <summary>Gets the capabilities of a surface on a physical device.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>The surface capabilities relevant to swapchain creation.</returns>
    VulkanSurfaceCapabilities GetSurfaceCapabilities(VulkanInstanceCommands instance, nint physicalDeviceHandle, nint surfaceHandle);
    /// <summary>Gets the format/color-space pairs a physical device supports for a surface.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>The supported surface formats.</returns>
    IReadOnlyList<VulkanSurfaceFormat> GetSurfaceFormats(VulkanInstanceCommands instance, nint physicalDeviceHandle, nint surfaceHandle);
    /// <summary>Determines whether a queue family of a physical device can present to a surface.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="queueFamilyIndex">The index of the queue family to test.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns><see langword="true"/> if the family supports presentation to the surface; otherwise, <see langword="false"/>.</returns>
    bool GetSurfaceSupport(VulkanInstanceCommands instance, nint physicalDeviceHandle, uint queueFamilyIndex, nint surfaceHandle);
}
