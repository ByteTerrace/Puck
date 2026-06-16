using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanGraphicsPipeline"/> together with its descriptor set layout and pipeline
/// layout, wiring up the supplied shaders, push constants, sampler bindings, and optional storage buffer.
/// </summary>
public interface IVulkanGraphicsPipelineFactory {
    /// <summary>Creates a graphics pipeline whose viewport and scissor are sized to the swapchain's extent.</summary>
    /// <param name="logicalDevice">The logical device the pipeline is created on.</param>
    /// <param name="renderPass">The render pass the pipeline is used with.</param>
    /// <param name="swapchain">The swapchain whose extent sizes the viewport and scissor.</param>
    /// <param name="vertexShaderModule">The vertex shader module.</param>
    /// <param name="fragmentShaderModule">The fragment shader module.</param>
    /// <param name="pushConstantBinding">The push constant range to expose, or <see langword="null"/> for none.</param>
    /// <param name="textureSamplerCount">The number of combined image-sampler descriptors in the texture array binding.</param>
    /// <param name="enableStorageBuffer">Whether to include a storage buffer binding in the descriptor set layout.</param>
    /// <returns>A new, owning <see cref="VulkanGraphicsPipeline"/>.</returns>
    VulkanGraphicsPipeline Create(
        VulkanLogicalDevice logicalDevice,
        VulkanRenderPass renderPass,
        VulkanSwapchain swapchain,
        VulkanShaderModule vertexShaderModule,
        VulkanShaderModule fragmentShaderModule,
        VulkanPushConstantBinding? pushConstantBinding = null,
        uint textureSamplerCount = 64,
        bool enableStorageBuffer = true
    );

    /// <summary>Creates a graphics pipeline whose viewport and scissor are sized to an explicit width and height (for offscreen targets).</summary>
    /// <param name="logicalDevice">The logical device the pipeline is created on.</param>
    /// <param name="renderPass">The render pass the pipeline is used with.</param>
    /// <param name="width">The viewport and scissor width, in pixels.</param>
    /// <param name="height">The viewport and scissor height, in pixels.</param>
    /// <param name="vertexShaderModule">The vertex shader module.</param>
    /// <param name="fragmentShaderModule">The fragment shader module.</param>
    /// <param name="pushConstantBinding">The push constant range to expose, or <see langword="null"/> for none.</param>
    /// <param name="textureSamplerCount">The number of combined image-sampler descriptors in the texture array binding.</param>
    /// <param name="enableStorageBuffer">Whether to include a storage buffer binding in the descriptor set layout.</param>
    /// <returns>A new, owning <see cref="VulkanGraphicsPipeline"/>.</returns>
    VulkanGraphicsPipeline Create(
        VulkanLogicalDevice logicalDevice,
        VulkanRenderPass renderPass,
        uint width,
        uint height,
        VulkanShaderModule vertexShaderModule,
        VulkanShaderModule fragmentShaderModule,
        VulkanPushConstantBinding? pushConstantBinding = null,
        uint textureSamplerCount = 64,
        bool enableStorageBuffer = true
    );
}
