using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native graphics pipeline (<c>VkPipeline</c>) handle together with its pipeline layout and
/// descriptor set layout, and destroys all three when disposed.
/// </summary>
public sealed class VulkanGraphicsPipeline : IGpuPipeline {
    private readonly IVulkanGraphicsPipelineApi m_graphicsPipelineApi;
    private readonly VulkanDescriptorSetGroups? m_setGroups;

    private bool m_disposed;

    /// <summary>Gets the native <c>VkDescriptorSetLayout</c> handle, or zero once disposed.</summary>
    public nint DescriptorSetLayoutHandle { get; private set; }
    /// <summary>Gets the command table of the logical device that owns the pipeline.</summary>
    public VulkanDeviceCommands Device { get; }
    /// <summary>Gets the native <c>VkDescriptorSetLayout</c> handle of each set of a pipeline created from a
    /// <see cref="GpuGraphicsPipelineDescription.Layout"/>, indexed by set number, or empty for any other pipeline or
    /// once disposed.</summary>
    public IReadOnlyList<nint> GroupLayoutHandles { get; private set; }
    /// <summary>Gets the native <c>VkPipeline</c> handle, or zero once the pipeline has been disposed.</summary>
    public nint Handle { get; private set; }
    /// <summary>Gets the native <c>VkPipelineLayout</c> handle, or zero once disposed.</summary>
    public nint LayoutHandle { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="VulkanGraphicsPipeline"/> class, taking ownership of an existing pipeline and its layouts.</summary>
    /// <param name="device">The command table of the logical device that owns the pipeline.</param>
    /// <param name="descriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle to own.</param>
    /// <param name="layoutHandle">The native <c>VkPipelineLayout</c> handle to own.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle to own.</param>
    /// <param name="graphicsPipelineApi">The API used to destroy the pipeline and layouts on disposal.</param>
    /// <param name="groupLayoutHandles">The native <c>VkDescriptorSetLayout</c> handle of each set of a pipeline created
    /// from a <see cref="GpuGraphicsPipelineDescription.Layout"/>, indexed by set number, to own; empty or
    /// <see langword="null"/> for any other pipeline.</param>
    /// <param name="setGroups">The device's set groups, which record <paramref name="groupLayoutHandles"/> for as long as
    /// the pipeline lives, or <see langword="null"/> for a pipeline of no group.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="graphicsPipelineApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="layoutHandle"/> or <paramref name="pipelineHandle"/> is zero.</exception>
    public VulkanGraphicsPipeline(
        VulkanDeviceCommands device,
        nint descriptorSetLayoutHandle,
        nint layoutHandle,
        nint pipelineHandle,
        IVulkanGraphicsPipelineApi graphicsPipelineApi,
        IReadOnlyList<nint>? groupLayoutHandles = null,
        VulkanDescriptorSetGroups? setGroups = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: graphicsPipelineApi);

        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: layoutHandle,
            handleDescription: "pipeline-layout",
            paramName: nameof(layoutHandle)
        );

        VulkanArgument.RequireHandle(
            handle: pipelineHandle,
            handleDescription: "graphics-pipeline",
            paramName: nameof(pipelineHandle)
        );

        Device = device;
        DescriptorSetLayoutHandle = descriptorSetLayoutHandle;
        GroupLayoutHandles = (groupLayoutHandles ?? []);
        LayoutHandle = layoutHandle;
        Handle = pipelineHandle;
        m_graphicsPipelineApi = graphicsPipelineApi;
        m_setGroups = setGroups;
        m_setGroups?.AddLayouts(setLayoutHandles: GroupLayoutHandles);
    }

    /// <summary>Destroys the owned pipeline, pipeline layout, and descriptor set layout. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_graphicsPipelineApi.DestroyPipeline(
            device: Device,
            pipelineHandle: Handle
        );
        VulkanPipelineLayouts.Destroy(
            descriptorSetLayoutHandle: DescriptorSetLayoutHandle,
            device: Device,
            pipelineLayoutHandle: LayoutHandle
        );
        m_setGroups?.RemoveLayouts(setLayoutHandles: GroupLayoutHandles);
        VulkanPipelineLayouts.DestroySets(
            device: Device,
            setLayoutHandles: GroupLayoutHandles
        );
        Handle = 0;
        DescriptorSetLayoutHandle = 0;
        GroupLayoutHandles = [];
        LayoutHandle = 0;
        m_disposed = true;
    }
}
