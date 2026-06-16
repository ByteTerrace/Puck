namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a swapchain to create: the device and surface it binds, and the resolved image, transform, and
/// presentation parameters.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="SurfaceHandle">The native <c>VkSurfaceKHR</c> handle the swapchain presents to.</param>
/// <param name="CompositeAlpha">The composite-alpha mode, as a <c>VkCompositeAlphaFlagBitsKHR</c> value.</param>
/// <param name="ImageExtentWidth">The width, in pixels, of the swapchain images.</param>
/// <param name="ImageExtentHeight">The height, in pixels, of the swapchain images.</param>
/// <param name="ImageFormat">The image format, as a <c>VkFormat</c> value.</param>
/// <param name="ImageColorSpace">The image color space, as a <c>VkColorSpaceKHR</c> value.</param>
/// <param name="ImageCount">The number of images in the swapchain.</param>
/// <param name="ImageUsage">A bitmask of <c>VkImageUsageFlagBits</c> describing the intended usage of the images.</param>
/// <param name="PresentMode">The presentation mode, as a <c>VkPresentModeKHR</c> value.</param>
/// <param name="PreTransform">The transform applied before presentation, as a <c>VkSurfaceTransformFlagBitsKHR</c> value.</param>
/// <param name="QueueFamilyIndices">The queue family indices that access the images when sharing is concurrent.</param>
/// <param name="SharingMode">The image sharing mode across queue families, as a <c>VkSharingMode</c> value.</param>
public readonly record struct VulkanSwapchainCreateRequest(
    nint DeviceHandle,
    nint SurfaceHandle,
    uint CompositeAlpha,
    uint ImageExtentWidth,
    uint ImageExtentHeight,
    uint ImageFormat,
    uint ImageColorSpace,
    uint ImageCount,
    uint ImageUsage,
    uint PresentMode,
    uint PreTransform,
    IReadOnlyList<uint> QueueFamilyIndices,
    uint SharingMode
);
