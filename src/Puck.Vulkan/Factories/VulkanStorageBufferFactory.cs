using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanStorageBufferFactory"/>: it creates a host-visible storage buffer of the
/// requested size and returns an owning <see cref="VulkanStorageBuffer"/>.
/// </summary>
public sealed class VulkanStorageBufferFactory : IVulkanStorageBufferFactory {
    private readonly IVulkanStorageBufferApi m_storageBufferApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanStorageBufferFactory"/> class.</summary>
    /// <param name="storageBufferApi">The storage-buffer API used to create and own the underlying buffer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storageBufferApi"/> is <see langword="null"/>.</exception>
    public VulkanStorageBufferFactory(IVulkanStorageBufferApi storageBufferApi) {
        ArgumentNullException.ThrowIfNull(argument: storageBufferApi);

        m_storageBufferApi = storageBufferApi;
    }

    /// <inheritdoc/>
    public VulkanStorageBuffer Create(
        VulkanInstance vulkanInstance,
        VulkanLogicalDevice logicalDevice,
        ulong sizeBytes,
        bool deviceLocal = false,
        bool indirectArgs = false
    ) {
        ArgumentNullException.ThrowIfNull(argument: vulkanInstance);
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);

        var handles = m_storageBufferApi.CreateStorageBuffer(request: new(
            DeviceHandle: logicalDevice.Handle,
            DeviceLocal: deviceLocal,
            IndirectArgs: indirectArgs,
            InstanceHandle: vulkanInstance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            SizeBytes: sizeBytes
        ));

        return new(
            bufferHandle: handles.BufferHandle,
            deviceHandle: logicalDevice.Handle,
            memoryHandle: handles.MemoryHandle,
            sizeBytes: sizeBytes,
            storageBufferApi: m_storageBufferApi
        );
    }
}
