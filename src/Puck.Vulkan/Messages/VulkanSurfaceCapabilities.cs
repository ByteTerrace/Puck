namespace Puck.Vulkan.Messages;

/// <summary>
/// A managed view of a surface's swapchain-relevant capabilities, distilled from
/// <see cref="Bindings.VkSurfaceCapabilitiesKhr"/> with the extents flattened to scalar components.
/// </summary>
/// <param name="MinImageCount">The minimum number of images a swapchain for the surface must contain.</param>
/// <param name="MaxImageCount">The maximum number of images a swapchain for the surface may contain, or zero for no limit.</param>
/// <param name="CurrentTransform">The surface's current transform, as a <c>VkSurfaceTransformFlagBitsKHR</c> value.</param>
/// <param name="CurrentExtentWidth">The current surface width, in pixels, or <c>0xFFFFFFFF</c> if determined by the swapchain.</param>
/// <param name="CurrentExtentHeight">The current surface height, in pixels, or <c>0xFFFFFFFF</c> if determined by the swapchain.</param>
/// <param name="MinImageExtentWidth">The smallest valid swapchain image width, in pixels.</param>
/// <param name="MinImageExtentHeight">The smallest valid swapchain image height, in pixels.</param>
/// <param name="MaxImageExtentWidth">The largest valid swapchain image width, in pixels.</param>
/// <param name="MaxImageExtentHeight">The largest valid swapchain image height, in pixels.</param>
/// <param name="SupportedCompositeAlpha">A bitmask of <c>VkCompositeAlphaFlagBitsKHR</c> values the surface supports.</param>
public readonly record struct VulkanSurfaceCapabilities(
    uint MinImageCount,
    uint MaxImageCount,
    uint CurrentTransform,
    uint CurrentExtentWidth,
    uint CurrentExtentHeight,
    uint MinImageExtentWidth,
    uint MinImageExtentHeight,
    uint MaxImageExtentWidth,
    uint MaxImageExtentHeight,
    uint SupportedCompositeAlpha
);
