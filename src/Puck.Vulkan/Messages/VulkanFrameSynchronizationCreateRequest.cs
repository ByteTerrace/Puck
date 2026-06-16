namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a synchronization primitive (fence or semaphore) to create.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="StartSignaled">Whether a created fence starts in the signaled state. Ignored for semaphores.</param>
public readonly record struct VulkanFrameSynchronizationCreateRequest(nint DeviceHandle, bool StartSignaled);
