using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for a graphics pipeline, created together with its descriptor set layout and
/// pipeline layout; the layouts are destroyed through <see cref="VulkanPipelineLayouts"/>.
/// </summary>
public interface IVulkanGraphicsPipelineApi {
    /// <summary>Creates a graphics pipeline together with its descriptor set layout and pipeline layout.</summary>
    /// <param name="request">The graphics pipeline creation parameters.</param>
    /// <param name="descriptorSetLayoutHandle">When this method returns, the created native <c>VkDescriptorSetLayout</c> handle.</param>
    /// <param name="pipelineLayoutHandle">When this method returns, the created native <c>VkPipelineLayout</c> handle.</param>
    /// <param name="pipelineHandle">When this method returns, the created native <c>VkPipeline</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the pipeline was created successfully.</returns>
    VkResult CreateGraphicsPipeline(
        VulkanGraphicsPipelineCreateRequest request,
        out nint descriptorSetLayoutHandle,
        out nint pipelineLayoutHandle,
        out nint pipelineHandle
    );
    /// <summary>Destroys a pipeline, skipping a zero handle; its layouts go through <see cref="VulkanPipelineLayouts.Destroy"/>.</summary>
    /// <param name="device">The command table of the logical device that owns the pipeline.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle to destroy.</param>
    void DestroyPipeline(VulkanDeviceCommands device, nint pipelineHandle);
}
