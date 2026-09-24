using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// A Vulkan <see cref="IGpuComputePipeline"/> owning its pipeline, pipeline-layout, and descriptor-set-layout
/// handles, destroying the pipeline through <see cref="IVulkanComputePipelineApi"/> and its layouts through
/// <see cref="VulkanPipelineLayouts"/> on dispose.
/// </summary>
public sealed class VulkanGpuComputePipeline : IGpuComputePipeline {
    private readonly IVulkanComputePipelineApi m_api;
    private readonly VulkanDeviceCommands m_device;

    private bool m_disposed;
    private nint m_pipeline;

    /// <summary>Initializes a new instance of the <see cref="VulkanGpuComputePipeline"/> class.</summary>
    /// <param name="api">The compute pipeline API used to destroy the handles.</param>
    /// <param name="device">The command table of the logical device the handles belong to.</param>
    /// <param name="descriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle.</param>
    /// <param name="layoutHandle">The native <c>VkPipelineLayout</c> handle.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle.</param>
    public VulkanGpuComputePipeline(IVulkanComputePipelineApi api, VulkanDeviceCommands device, nint descriptorSetLayoutHandle, nint layoutHandle, nint pipelineHandle) {
        m_api = api;
        m_device = device;
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
        m_api.DestroyPipeline(
            device: m_device,
            pipelineHandle: m_pipeline
        );
        VulkanPipelineLayouts.Destroy(
            descriptorSetLayoutHandle: DescriptorSetLayoutHandle,
            device: m_device,
            pipelineLayoutHandle: LayoutHandle
        );
        m_pipeline = 0;
    }
}
