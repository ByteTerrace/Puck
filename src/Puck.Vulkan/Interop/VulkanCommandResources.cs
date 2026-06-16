using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a command pool and the command buffers allocated from it, destroying the pool (and with it the
/// buffers) when disposed.
/// </summary>
public sealed class VulkanCommandResources : IDisposable {
    private readonly IVulkanCommandResourcesApi m_commandResourcesApi;
    private bool m_disposed;

    /// <summary>Gets the native <c>VkCommandBuffer</c> handles allocated from the pool. Empty once disposed.</summary>
    public IReadOnlyList<nint> CommandBufferHandles { get; private set; }
    /// <summary>Gets the native <c>VkCommandPool</c> handle, or zero once disposed.</summary>
    public nint CommandPoolHandle { get; private set; }
    /// <summary>Gets the native <c>VkDevice</c> handle that owns the resources.</summary>
    public nint DeviceHandle { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanCommandResources"/> class, taking ownership of an existing command pool and its command buffers.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the resources.</param>
    /// <param name="commandBufferHandles">The native <c>VkCommandBuffer</c> handles allocated from the pool.</param>
    /// <param name="commandPoolHandle">The native <c>VkCommandPool</c> handle to own.</param>
    /// <param name="commandResourcesApi">The API used to destroy the command pool on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commandBufferHandles"/> or <paramref name="commandResourcesApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/> or <paramref name="commandPoolHandle"/> is zero.</exception>
    public VulkanCommandResources(
        nint deviceHandle,
        IReadOnlyList<nint> commandBufferHandles,
        nint commandPoolHandle,
        IVulkanCommandResourcesApi commandResourcesApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: commandBufferHandles);
        ArgumentNullException.ThrowIfNull(argument: commandResourcesApi);

        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandPoolHandle) {
            throw new ArgumentException(
                message: "Vulkan command-pool handle must be non-zero.",
                paramName: nameof(commandPoolHandle)
            );
        }

        DeviceHandle = deviceHandle;
        CommandBufferHandles = commandBufferHandles;
        CommandPoolHandle = commandPoolHandle;
        m_commandResourcesApi = commandResourcesApi;
    }

    /// <summary>Destroys the owned command pool and the command buffers allocated from it. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (0 != CommandPoolHandle) {
            m_commandResourcesApi.DestroyCommandPool(
                commandPoolHandle: CommandPoolHandle,
                deviceHandle: DeviceHandle
            );
            CommandPoolHandle = 0;
        }

        CommandBufferHandles = [];
        m_disposed = true;
    }
}
