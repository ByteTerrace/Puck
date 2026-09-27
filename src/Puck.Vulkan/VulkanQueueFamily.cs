namespace Puck.Vulkan;

/// <summary>
/// The special queue family indices a barrier names in place of a queue family: none, for a barrier that transfers no
/// ownership, and the external family, for a resource another API or device uses between a release and an acquire.
/// </summary>
public static class VulkanQueueFamily {
    /// <summary>The <c>VK_QUEUE_FAMILY_EXTERNAL</c> value: the owner of a resource while a device outside this instance
    /// (another API, as a Direct3D 11 reader of an imported texture) uses it.</summary>
    public const uint External = 0xFFFFFFFE;
    /// <summary>The <c>VK_QUEUE_FAMILY_IGNORED</c> value: a barrier that transfers no ownership.</summary>
    public const uint Ignored = 0xFFFFFFFF;
}
