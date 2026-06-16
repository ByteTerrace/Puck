using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native graphics pipeline (<c>VkPipeline</c>) handle together with its pipeline layout and
/// descriptor set layout, and destroys all three when disposed.
/// </summary>
public sealed class VulkanGraphicsPipeline : IDisposable {
    private bool m_disposed;
    private readonly IVulkanGraphicsPipelineApi m_graphicsPipelineApi;

    /// <summary>Gets the native <c>VkDescriptorSetLayout</c> handle, or zero once disposed.</summary>
    public nint DescriptorSetLayoutHandle { get; private set; }
    /// <summary>Gets the native <c>VkDevice</c> handle that owns the pipeline.</summary>
    public nint DeviceHandle { get; }
    /// <summary>Gets the native <c>VkPipeline</c> handle, or zero once the pipeline has been disposed.</summary>
    public nint Handle { get; private set; }
    /// <summary>Gets the native <c>VkPipelineLayout</c> handle, or zero once disposed.</summary>
    public nint LayoutHandle { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="VulkanGraphicsPipeline"/> class, taking ownership of an existing pipeline and its layouts.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the pipeline.</param>
    /// <param name="descriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle to own.</param>
    /// <param name="layoutHandle">The native <c>VkPipelineLayout</c> handle to own.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle to own.</param>
    /// <param name="graphicsPipelineApi">The API used to destroy the pipeline and layouts on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graphicsPipelineApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/>, <paramref name="layoutHandle"/>, or <paramref name="pipelineHandle"/> is zero.</exception>
    public VulkanGraphicsPipeline(
        nint deviceHandle,
        nint descriptorSetLayoutHandle,
        nint layoutHandle,
        nint pipelineHandle,
        IVulkanGraphicsPipelineApi graphicsPipelineApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: graphicsPipelineApi);

        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == layoutHandle) {
            throw new ArgumentException(
                message: "Vulkan pipeline-layout handle must be non-zero.",
                paramName: nameof(layoutHandle)
            );
        }

        if (0 == pipelineHandle) {
            throw new ArgumentException(
                message: "Vulkan graphics-pipeline handle must be non-zero.",
                paramName: nameof(pipelineHandle)
            );
        }

        DeviceHandle = deviceHandle;
        DescriptorSetLayoutHandle = descriptorSetLayoutHandle;
        LayoutHandle = layoutHandle;
        Handle = pipelineHandle;
        m_graphicsPipelineApi = graphicsPipelineApi;
    }

    /// <summary>Destroys the owned pipeline, pipeline layout, and descriptor set layout. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (0 != Handle) {
            m_graphicsPipelineApi.DestroyPipeline(
                deviceHandle: DeviceHandle,
                pipelineHandle: Handle
            );
            Handle = 0;
        }

        if (0 != DescriptorSetLayoutHandle) {
            m_graphicsPipelineApi.DestroyDescriptorSetLayout(
                deviceHandle: DeviceHandle,
                descriptorSetLayoutHandle: DescriptorSetLayoutHandle
            );
            DescriptorSetLayoutHandle = 0;
        }

        if (0 != LayoutHandle) {
            m_graphicsPipelineApi.DestroyPipelineLayout(
                deviceHandle: DeviceHandle,
                pipelineLayoutHandle: LayoutHandle
            );
            LayoutHandle = 0;
        }

        m_disposed = true;
    }
}
