using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// A Vulkan <see cref="IGpuComputePipeline"/> owning its pipeline, pipeline-layout, and descriptor-set-layout
/// handles, destroying them through <see cref="IVulkanComputePipelineApi"/> on dispose.
/// </summary>
public sealed class VulkanGpuComputePipeline : IGpuComputePipeline {
    private readonly IVulkanComputePipelineApi m_api;
    private readonly nint m_deviceHandle;
    private bool m_disposed;
    private nint m_pipeline;

    /// <summary>Initializes a new instance of the <see cref="VulkanGpuComputePipeline"/> class.</summary>
    /// <param name="api">The compute pipeline API used to destroy the handles.</param>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle the handles belong to.</param>
    /// <param name="descriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle.</param>
    /// <param name="layoutHandle">The native <c>VkPipelineLayout</c> handle.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle.</param>
    public VulkanGpuComputePipeline(IVulkanComputePipelineApi api, nint deviceHandle, nint descriptorSetLayoutHandle, nint layoutHandle, nint pipelineHandle) {
        m_api = api;
        m_deviceHandle = deviceHandle;
        m_pipeline = pipelineHandle;
        DescriptorSetLayoutHandle = descriptorSetLayoutHandle;
        LayoutHandle = layoutHandle;
    }

    /// <inheritdoc/>
    public nint DescriptorSetLayoutHandle { get; }
    /// <inheritdoc/>
    public nint Handle => m_pipeline;
    /// <inheritdoc/>
    public nint LayoutHandle { get; }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_api.DestroyPipeline(deviceHandle: m_deviceHandle, pipelineHandle: m_pipeline);
        m_api.DestroyPipelineLayout(deviceHandle: m_deviceHandle, pipelineLayoutHandle: LayoutHandle);
        m_api.DestroyDescriptorSetLayout(deviceHandle: m_deviceHandle, descriptorSetLayoutHandle: DescriptorSetLayoutHandle);
        m_pipeline = 0;
    }
}
