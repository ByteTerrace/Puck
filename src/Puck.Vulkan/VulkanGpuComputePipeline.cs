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
    private readonly VulkanDescriptorSetGroups? m_setGroups;

    private bool m_disposed;
    private nint m_pipeline;

    /// <summary>Initializes a new instance of the <see cref="VulkanGpuComputePipeline"/> class.</summary>
    /// <param name="api">The compute pipeline API used to destroy the handles.</param>
    /// <param name="device">The command table of the logical device the handles belong to.</param>
    /// <param name="descriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle.</param>
    /// <param name="layoutHandle">The native <c>VkPipelineLayout</c> handle.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle.</param>
    /// <param name="groupLayoutHandles">The native <c>VkDescriptorSetLayout</c> handle of each set of a pipeline created
    /// from a <see cref="GpuComputePipelineDescription.Layout"/>, indexed by set number, which the pipeline owns; empty
    /// or <see langword="null"/> for any other pipeline.</param>
    /// <param name="setGroups">The device's set groups, which record <paramref name="groupLayoutHandles"/> for as long as
    /// the pipeline lives, or <see langword="null"/> for a pipeline of no group.</param>
    public VulkanGpuComputePipeline(IVulkanComputePipelineApi api, VulkanDeviceCommands device, nint descriptorSetLayoutHandle, nint layoutHandle, nint pipelineHandle, IReadOnlyList<nint>? groupLayoutHandles = null, VulkanDescriptorSetGroups? setGroups = null) {
        m_api = api;
        m_device = device;
        m_pipeline = pipelineHandle;
        m_setGroups = setGroups;
        DescriptorSetLayoutHandle = descriptorSetLayoutHandle;
        GroupLayoutHandles = (groupLayoutHandles ?? []);
        LayoutHandle = layoutHandle;
        m_setGroups?.AddLayouts(setLayoutHandles: GroupLayoutHandles);
    }

    /// <inheritdoc/>
    public nint DescriptorSetLayoutHandle { get; }
    /// <inheritdoc/>
    public IReadOnlyList<nint> GroupLayoutHandles { get; }
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
        m_setGroups?.RemoveLayouts(setLayoutHandles: GroupLayoutHandles);
        VulkanPipelineLayouts.DestroySets(
            device: m_device,
            setLayoutHandles: GroupLayoutHandles
        );
        m_pipeline = 0;
    }
}
