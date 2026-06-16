namespace Puck.Vulkan;

/// <summary>
/// The result of a frame presentation attempt: the outcome category and, when a frame was presented, the
/// index of the presented swapchain image.
/// </summary>
/// <param name="Result">The outcome category.</param>
/// <param name="ImageIndex">The index of the presented swapchain image, or <see langword="null"/> when no frame was presented.</param>
public sealed record VulkanFramePresentationOutcome(VulkanFramePresentationResult Result, uint? ImageIndex) {
    /// <summary>Creates an outcome carrying a result category but no image index.</summary>
    /// <param name="result">The outcome category.</param>
    /// <returns>An outcome with the given result and a <see langword="null"/> image index.</returns>
    public static VulkanFramePresentationOutcome FromResult(VulkanFramePresentationResult result) {
        return new VulkanFramePresentationOutcome(
            ImageIndex: null,
            Result: result
        );
    }
    /// <summary>Creates a <see cref="VulkanFramePresentationResult.Presented"/> outcome carrying the presented image index.</summary>
    /// <param name="imageIndex">The index of the presented swapchain image.</param>
    /// <returns>A presented outcome with the given image index.</returns>
    public static VulkanFramePresentationOutcome Presented(uint imageIndex) {
        return new VulkanFramePresentationOutcome(
            ImageIndex: imageIndex,
            Result: VulkanFramePresentationResult.Presented
        );
    }
}
