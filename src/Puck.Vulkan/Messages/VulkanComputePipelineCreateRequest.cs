using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a compute pipeline to create: the compute shader module, the descriptor-set-layout bindings the
/// kernel reads (set 0), and an optional push-constant range.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="ShaderModuleHandle">The native <c>VkShaderModule</c> handle for the compute kernel (entry point <c>main</c>).</param>
/// <param name="DescriptorBindings">The set-0 descriptor-set-layout bindings the kernel accesses, or empty for none.</param>
/// <param name="PushConstantSize">The push-constant range size in bytes, or zero for no push constants.</param>
/// <param name="PushConstantStageFlags">The <c>VkShaderStageFlagBits</c> the push-constant range is visible to (non-zero when <paramref name="PushConstantSize"/> is non-zero).</param>
public readonly record struct VulkanComputePipelineCreateRequest(
    nint DeviceHandle,
    nint ShaderModuleHandle,
    IReadOnlyList<VkDescriptorSetLayoutBinding> DescriptorBindings,
    uint PushConstantSize,
    uint PushConstantStageFlags
);
