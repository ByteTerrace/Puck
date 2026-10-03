using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;

namespace Puck.DirectX.Apis;

/// <summary>
/// The software implementation of <see cref="IDirectXDeviceApi"/>: every device it creates is on the software (WARP)
/// renderer, whatever adapter it is asked for, and each is read through <see cref="DirectXNativeDeviceApi"/>. A
/// <see cref="DirectXDeviceContext"/> handed this API runs on the CPU, on a host with no hardware adapter as on one with,
/// so constructing it is not a way onto the GPU as constructing <see cref="DirectXNativeDeviceApi"/> is.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public sealed class DirectXWarpDeviceApi : IDirectXDeviceApi {
    private readonly DirectXNativeDeviceApi m_native = new();

    /// <inheritdoc/>
    /// <remarks>The device is on the software renderer; <paramref name="adapterLuid"/> is not read.</remarks>
    public DirectXDevice CreateDevice(long adapterLuid, DirectXFeatureLevel minimumFeatureLevel) =>
        m_native.CreateWarpDevice(minimumFeatureLevel: minimumFeatureLevel);
    /// <inheritdoc/>
    public DirectXDevice CreateWarpDevice(DirectXFeatureLevel minimumFeatureLevel) =>
        m_native.CreateWarpDevice(minimumFeatureLevel: minimumFeatureLevel);
    /// <inheritdoc/>
    public long GetAdapterLuid(nint deviceHandle) =>
        m_native.GetAdapterLuid(deviceHandle: deviceHandle);
    /// <inheritdoc/>
    public GpuDeviceCapabilities GetDeviceCapabilities(nint deviceHandle) =>
        m_native.GetDeviceCapabilities(deviceHandle: deviceHandle);
    /// <inheritdoc/>
    public GpuDeviceIdentity GetDeviceIdentity(nint deviceHandle) =>
        m_native.GetDeviceIdentity(deviceHandle: deviceHandle);
    /// <inheritdoc/>
    public GpuMemoryProfile GetMemoryProfile(nint deviceHandle) =>
        m_native.GetMemoryProfile(deviceHandle: deviceHandle);
}
