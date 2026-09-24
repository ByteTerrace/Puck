using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a synchronization primitive (fence or semaphore) to create.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="StartSignaled">Whether a created fence starts in the signaled state. Ignored for semaphores.</param>
public readonly record struct VulkanFrameSynchronizationCreateRequest(VulkanDeviceCommands Device, bool StartSignaled);
