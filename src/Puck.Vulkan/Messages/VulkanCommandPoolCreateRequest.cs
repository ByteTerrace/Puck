using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a command pool to create for a given queue family.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="QueueFamilyIndex">The queue family that command buffers from the pool are submitted to.</param>
public readonly record struct VulkanCommandPoolCreateRequest(VulkanDeviceCommands Device, uint QueueFamilyIndex);
