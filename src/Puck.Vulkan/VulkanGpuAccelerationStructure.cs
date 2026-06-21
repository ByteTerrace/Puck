using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuAccelerationStructure"/> over <see cref="IVulkanWorldAccelerationApi"/>: it resolves the
/// instance/physical-device/logical-device handles from the Vulkan device context and forwards the create, instance
/// write, build, and teardown to the world-acceleration builder. <see cref="TlasReference"/> is the
/// <c>VkAccelerationStructureKHR</c> handle (the descriptor allocator binds it directly).
/// </summary>
public sealed class VulkanGpuAccelerationStructure : IGpuAccelerationStructure {
    private readonly IVulkanWorldAccelerationApi m_api;
    private readonly nint m_deviceHandle;
    private readonly nint m_instanceHandle;
    private readonly nint m_physicalDeviceHandle;

    private bool m_created;
    private bool m_disposed;
    private VulkanWorldAccelerationResources m_resources;

    /// <summary>Initializes a new instance of the <see cref="VulkanGpuAccelerationStructure"/> class.</summary>
    /// <param name="api">The world-acceleration builder.</param>
    /// <param name="deviceContext">The Vulkan device context the structures are created on.</param>
    public VulkanGpuAccelerationStructure(IVulkanWorldAccelerationApi api, IVulkanDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(deviceContext);

        m_api = api;
        m_deviceHandle = deviceContext.LogicalDevice.Handle;
        m_instanceHandle = deviceContext.Instance.Handle;
        m_physicalDeviceHandle = deviceContext.PhysicalDevice.Handle;
    }

    /// <inheritdoc/>
    public bool IsSupported => m_api.SupportsDevice(deviceHandle: m_deviceHandle);
    /// <inheritdoc/>
    public nint TlasReference => m_resources.TlasHandle;

    /// <inheritdoc/>
    public void EnsureCreated(uint maxInstanceCount) {
        if (m_created) {
            return;
        }

        m_resources = m_api.CreateResources(request: new VulkanWorldAccelerationCreateRequest(
            DeviceHandle: m_deviceHandle,
            InstanceHandle: m_instanceHandle,
            MaxInstanceCount: maxInstanceCount,
            PhysicalDeviceHandle: m_physicalDeviceHandle
        ));
        m_created = true;
    }

    /// <inheritdoc/>
    public void WriteInstance(int index, float halfExtentX, float halfExtentY, float halfExtentZ, float centerX, float centerY, float centerZ, uint instanceIndex, uint visibilityMask) =>
        m_api.WriteInstance(
            blasDeviceAddress: m_resources.BlasDeviceAddress,
            index: index,
            instanceBufferMappedPointer: m_resources.InstanceBufferMappedPointer,
            instanceCustomIndex: instanceIndex,
            scaleX: halfExtentX,
            scaleY: halfExtentY,
            scaleZ: halfExtentZ,
            visibilityMask: visibilityMask,
            worldCenterX: centerX,
            worldCenterY: centerY,
            worldCenterZ: centerZ
        );

    /// <inheritdoc/>
    public void RecordBuild(nint commandBufferHandle, uint instanceCount, bool includeBlasBuild) =>
        m_api.RecordWorldAccelerationBuild(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: m_deviceHandle,
            includeBlasBuild: includeBlasBuild,
            instanceCount: instanceCount,
            resources: in m_resources
        );

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (m_created) {
            m_api.DestroyResources(deviceHandle: m_deviceHandle, resources: m_resources);
        }
    }
}
