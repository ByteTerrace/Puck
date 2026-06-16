using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanVertexBufferFactory"/>: it creates a vertex buffer sized to the supplied
/// data, uploads the data, and returns an owning <see cref="VulkanVertexBuffer"/>.
/// </summary>
public sealed class VulkanVertexBufferFactory : IVulkanVertexBufferFactory {
    private readonly IVulkanVertexBufferApi m_vertexBufferApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanVertexBufferFactory"/> class.</summary>
    /// <param name="vertexBufferApi">The vertex-buffer API used to create and own the underlying buffer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vertexBufferApi"/> is <see langword="null"/>.</exception>
    public VulkanVertexBufferFactory(IVulkanVertexBufferApi vertexBufferApi) {
        ArgumentNullException.ThrowIfNull(argument: vertexBufferApi);

        m_vertexBufferApi = vertexBufferApi;
    }

    /// <inheritdoc/>
    public VulkanVertexBuffer Create(
        VulkanInstance vulkanInstance,
        VulkanLogicalDevice logicalDevice,
        byte[] vertexData
    ) {
        ArgumentNullException.ThrowIfNull(argument: vulkanInstance);
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);
        ArgumentNullException.ThrowIfNull(argument: vertexData);

        var handles = m_vertexBufferApi.CreateVertexBuffer(
            request: new(
                DeviceHandle: logicalDevice.Handle,
                InstanceHandle: vulkanInstance.Handle,
                PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle
            ),
            vertexData: vertexData
        );

        return new(
            bufferHandle: handles.BufferHandle,
            deviceHandle: logicalDevice.Handle,
            memoryHandle: handles.MemoryHandle,
            vertexBufferApi: m_vertexBufferApi
        );
    }
}
