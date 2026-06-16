namespace Puck.Vulkan.Messages;

/// <summary>
/// The combined swapchain support of a surface on a physical device: its capabilities, the formats it
/// accepts, and the present modes it offers.
/// </summary>
/// <param name="Capabilities">The surface's swapchain-relevant capabilities.</param>
/// <param name="SurfaceFormats">The format/color-space pairs the surface supports.</param>
/// <param name="PresentModes">The presentation modes the surface supports, as <c>VkPresentModeKHR</c> values.</param>
public readonly record struct VulkanSwapchainSupportDetails(
    VulkanSurfaceCapabilities Capabilities,
    IReadOnlyList<VulkanSurfaceFormat> SurfaceFormats,
    IReadOnlyList<uint> PresentModes
) {
    /// <summary>Gets a value indicating whether the surface supports at least one format and one present mode, and so can back a swapchain.</summary>
    public bool IsComplete => ((SurfaceFormats.Count > 0) && (PresentModes.Count > 0));
}
