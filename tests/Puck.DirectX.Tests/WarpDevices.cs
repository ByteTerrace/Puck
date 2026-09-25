using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Contexts whose every device is the software (WARP) renderer, created without the debug layer, for laws that
/// need a real device but no adapter.</summary>
[SupportedOSPlatform("windows10.0.10240")]
internal static class WarpDevices {
    /// <summary>Returns a context whose software device is already created; skips the calling law when the host has
    /// none that meets the device floor.</summary>
    /// <param name="memory">The device-local counts the context's device joins, or <see langword="null"/>.</param>
    /// <returns>The context, owned by the caller.</returns>
    public static DirectXDeviceContext Context(GpuDeviceMemoryWork? memory = null) {
        var context = new DirectXDeviceContext(
            adapterLuid: 1L,
            deviceApi: new Api(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        ) {
            Memory = memory,
        };

        try {
            _ = context.Device;
        } catch (GpuDeviceUnavailableException exception) {
            context.Dispose();
            Assert.Skip(reason: $"no Direct3D 12 software device on this host: {exception.Message}");
        }

        return context;
    }

    // Creates every device on the software renderer and reads it through the native API.
    private sealed class Api : IDirectXDeviceApi {
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
}
