using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for a graphics pipeline and its associated objects (descriptor set layout
/// and pipeline layout), which are created together and destroyed individually.
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
    /// <summary>Destroys a descriptor set layout.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the layout.</param>
    /// <param name="descriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle to destroy.</param>
    void DestroyDescriptorSetLayout(nint deviceHandle, nint descriptorSetLayoutHandle);
    /// <summary>Destroys a pipeline.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the pipeline.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle to destroy.</param>
    void DestroyPipeline(nint deviceHandle, nint pipelineHandle);
    /// <summary>Destroys a pipeline layout.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the pipeline layout.</param>
    /// <param name="pipelineLayoutHandle">The native <c>VkPipelineLayout</c> handle to destroy.</param>
    void DestroyPipelineLayout(nint deviceHandle, nint pipelineLayoutHandle);
}
