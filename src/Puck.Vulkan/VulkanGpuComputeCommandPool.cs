using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// A Vulkan <see cref="IGpuComputeCommandPool"/> wrapping a single-command-buffer <see cref="VulkanCommandResources"/>.
/// </summary>
public sealed class VulkanGpuComputeCommandPool : IGpuComputeCommandPool {
    private readonly VulkanCommandResources m_commandResources;

    /// <summary>Initializes a new instance of the <see cref="VulkanGpuComputeCommandPool"/> class.</summary>
    /// <param name="commandResources">The command resources to wrap (its first command buffer is exposed).</param>
    public VulkanGpuComputeCommandPool(VulkanCommandResources commandResources) {
        m_commandResources = commandResources;
        CommandBufferHandle = commandResources.CommandBufferHandles[0];
    }

    /// <inheritdoc/>
    public nint CommandBufferHandle { get; }

    /// <inheritdoc/>
    public void Dispose() {
        m_commandResources.Dispose();
    }
}
