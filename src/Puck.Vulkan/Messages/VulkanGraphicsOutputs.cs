namespace Puck.Vulkan;

/// <summary>
/// What a graphics pipeline writes: how many color attachments its render pass has, each blended the same way, how it
/// tests and writes the render pass's depth attachment, and which way clip-space y points.
/// </summary>
/// <param name="ColorAttachmentCount">The render pass's color attachment count; one blend state is made per
/// attachment.</param>
/// <param name="AlphaBlend">Whether a fragment blends alpha-over what the attachment holds (source alpha, one minus
/// source alpha) rather than replacing it.</param>
/// <param name="DepthCompareOp">The depth test's <c>VkCompareOp</c> for a render pass with a depth attachment, which a
/// passing fragment writes; <see langword="null"/> for a render pass without one.</param>
/// <param name="ClipSpaceYUp">Whether clip-space +y is the top of the attachment, as in Direct3D, through a viewport of
/// negative height, rather than the bottom, Vulkan's own convention.</param>
public readonly record struct VulkanGraphicsOutputs(uint ColorAttachmentCount, bool AlphaBlend, uint? DepthCompareOp = null, bool ClipSpaceYUp = false);
