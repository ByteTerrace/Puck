using System.Runtime.Versioning;
using Puck.DirectX.Messages;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuAccelerationStructure"/> over <see cref="DirectXWorldAccelerationApi"/> (DXR 1.1). It
/// forwards the create, instance write, build, and teardown to the world-acceleration builder.
/// <see cref="TlasReference"/> is the top-level structure's GPU virtual address (the descriptor allocator binds it as
/// a raytracing-acceleration-structure SRV).
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class DirectXGpuAccelerationStructure : IGpuAccelerationStructure {
    private readonly DirectXWorldAccelerationApi m_api;
    private readonly nint m_deviceHandle;
    private bool m_created;
    private bool m_disposed;
    private DirectXWorldAccelerationResources m_resources;

    /// <summary>Initializes a new instance of the <see cref="DirectXGpuAccelerationStructure"/> class.</summary>
    /// <param name="api">The world-acceleration builder.</param>
    /// <param name="deviceHandle">The native <c>ID3D12Device</c> handle the structures are created on.</param>
    public DirectXGpuAccelerationStructure(DirectXWorldAccelerationApi api, nint deviceHandle) {
        ArgumentNullException.ThrowIfNull(api);

        m_api = api;
        m_deviceHandle = deviceHandle;
    }

    /// <inheritdoc/>
    public bool IsSupported => m_api.SupportsDevice(deviceHandle: m_deviceHandle);
    /// <inheritdoc/>
    public nint TlasReference => (nint)m_resources.TlasGpuAddress;

    /// <inheritdoc/>
    public void EnsureCreated(uint maxInstanceCount) {
        if (m_created) {
            return;
        }

        m_resources = m_api.CreateResources(deviceHandle: m_deviceHandle, maxInstanceCount: maxInstanceCount);
        m_created = true;
    }

    /// <inheritdoc/>
    public void WriteInstance(int index, float halfExtentX, float halfExtentY, float halfExtentZ, float centerX, float centerY, float centerZ, uint instanceIndex, uint visibilityMask) =>
        m_api.WriteInstance(
            blasGpuAddress: m_resources.BlasGpuAddress,
            index: index,
            instanceBufferMappedPointer: m_resources.InstanceBufferMappedPointer,
            instanceId: instanceIndex,
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
            m_api.DestroyResources(resources: in m_resources);
        }
    }
}
