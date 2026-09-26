using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;

namespace Puck.Testing;

/// <summary>Creates every Direct3D 12 device on the software (WARP) renderer, whatever adapter it is asked for, and
/// reads each one through the native API: the device API a device law hands a <c>DirectXDeviceContext</c> to run on a
/// host with no adapter.</summary>
[SupportedOSPlatform("windows8.1")]
internal sealed class WarpDeviceApi : IDirectXDeviceApi {
    private readonly DirectXNativeDeviceApi m_native = new();

    public DirectXDevice CreateDevice(long adapterLuid, DirectXFeatureLevel minimumFeatureLevel) =>
        m_native.CreateWarpDevice(minimumFeatureLevel: minimumFeatureLevel);
    public DirectXDevice CreateWarpDevice(DirectXFeatureLevel minimumFeatureLevel) =>
        m_native.CreateWarpDevice(minimumFeatureLevel: minimumFeatureLevel);
    public long GetAdapterLuid(nint deviceHandle) =>
        m_native.GetAdapterLuid(deviceHandle: deviceHandle);
    public GpuDeviceCapabilities GetDeviceCapabilities(nint deviceHandle) =>
        m_native.GetDeviceCapabilities(deviceHandle: deviceHandle);
    public GpuDeviceIdentity GetDeviceIdentity(nint deviceHandle) =>
        m_native.GetDeviceIdentity(deviceHandle: deviceHandle);
    public GpuMemoryProfile GetMemoryProfile(nint deviceHandle) =>
        m_native.GetMemoryProfile(deviceHandle: deviceHandle);
    public DirectXFeatureLevel? ProbeMaxFeatureLevel(long adapterLuid) =>
        m_native.ProbeMaxFeatureLevel(adapterLuid: adapterLuid);
}
