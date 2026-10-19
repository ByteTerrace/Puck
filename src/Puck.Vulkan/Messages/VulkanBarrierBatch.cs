namespace Puck.Vulkan.Messages;

/// <summary>One image layout transition of a batch of barriers (<c>VkImageMemoryBarrier</c> over a 2D, single-level,
/// single-layer image).</summary>
/// <param name="Image">The native <c>VkImage</c> handle.</param>
/// <param name="AspectMask">The image's aspect, as <c>VkImageAspectFlags</c>.</param>
/// <param name="OldLayout">The layout it is in, as a <c>VkImageLayout</c> value.</param>
/// <param name="NewLayout">The layout it moves to.</param>
/// <param name="SourceAccessMask">The prior accesses, as <c>VkAccessFlagBits</c>.</param>
/// <param name="DestinationAccessMask">The following accesses.</param>
public readonly record struct VulkanImageBarrier(nint Image, uint AspectMask, uint OldLayout, uint NewLayout, uint SourceAccessMask, uint DestinationAccessMask);
/// <summary>One buffer memory barrier of a batch of barriers, over the whole buffer.</summary>
/// <param name="Buffer">The native <c>VkBuffer</c> handle.</param>
/// <param name="SourceAccessMask">The prior accesses, as <c>VkAccessFlagBits</c>.</param>
/// <param name="DestinationAccessMask">The following accesses.</param>
public readonly record struct VulkanBufferBarrier(nint Buffer, uint SourceAccessMask, uint DestinationAccessMask);
