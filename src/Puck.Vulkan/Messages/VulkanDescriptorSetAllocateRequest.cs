using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a single descriptor set to allocate: the pool to allocate from and the layout of the set.
/// </summary>
/// <param name="DescriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle describing the set.</param>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="PoolHandle">The native <c>VkDescriptorPool</c> handle to allocate from.</param>
public readonly record struct VulkanDescriptorSetAllocateRequest(
    nint DescriptorSetLayoutHandle,
    VulkanDeviceCommands Device,
    nint PoolHandle
);
