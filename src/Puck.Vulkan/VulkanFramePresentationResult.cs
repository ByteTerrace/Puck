namespace Puck.Vulkan;

/// <summary>
/// Specifies the outcome of a frame presentation attempt and the recovery action, if any, the caller must take.
/// </summary>
public enum VulkanFramePresentationResult {
    /// <summary>The frame was rendered and presented; the outcome's image index is valid.</summary>
    Presented = 0,
    /// <summary>The frame was not ready this tick; no work was submitted. Try again on the next tick.</summary>
    Skipped = 1,
    /// <summary>The swapchain is out of date or suboptimal; rebuild the swapchain and the resources derived from it, then retry.</summary>
    RecreatePresentationResources = 2,
    /// <summary>The device or surface was lost; tear down and rebuild all Vulkan resources.</summary>
    ResetVulkanResources = 3
}
