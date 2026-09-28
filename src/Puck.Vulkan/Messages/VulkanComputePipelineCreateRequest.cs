using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a compute pipeline to create: the compute shader module, the descriptor-set-layout bindings the
/// kernel reads (set 0), and an optional push-constant range.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="ShaderModuleHandle">The native <c>VkShaderModule</c> handle for the compute kernel (entry point <c>main</c>).</param>
/// <param name="DescriptorBindings">The set-0 descriptor-set-layout bindings the kernel accesses, or empty for none.</param>
/// <param name="PushConstantSize">The push-constant range size in bytes, or zero for no push constants.</param>
/// <param name="PushConstantStageFlags">The <c>VkShaderStageFlagBits</c> the push-constant range is visible to (non-zero when <paramref name="PushConstantSize"/> is non-zero).</param>
/// <param name="PipelineCache">The device's pipeline cache, passed to the driver and counting the creation, or
/// <see langword="null"/> for a device created without one.</param>
/// <param name="PipelineLayoutHandle">A native <c>VkPipelineLayout</c> the caller created and owns, which the pipeline
/// is created with instead of a layout over <paramref name="DescriptorBindings"/> and the push-constant range; zero to
/// create that layout. With a layout given, the bindings are empty and the push-constant size zero.</param>
public readonly record struct VulkanComputePipelineCreateRequest(
    VulkanDeviceCommands Device,
    nint ShaderModuleHandle,
    IReadOnlyList<VkDescriptorSetLayoutBinding> DescriptorBindings,
    uint PushConstantSize,
    uint PushConstantStageFlags,
    VulkanPipelineCache? PipelineCache,
    nint PipelineLayoutHandle = 0
);
