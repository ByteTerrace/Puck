using System.Runtime.Versioning;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuAccelerationStructureFactory"/> for Direct3D 12 (DXR 1.1), pairing a new
/// <see cref="DirectXWorldAccelerationApi"/> with the device handle in a <see cref="DirectXGpuAccelerationStructure"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class DirectXGpuAccelerationStructureFactory : IGpuAccelerationStructureFactory {
    /// <inheritdoc/>
    public IGpuAccelerationStructure Create(IGpuDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        return new DirectXGpuAccelerationStructure(
            api: new DirectXWorldAccelerationApi(),
            deviceHandle: deviceContext.DeviceHandle
        );
    }
}
