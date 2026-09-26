using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Interop;
using Puck.Testing;
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
            deviceApi: new WarpDeviceApi(),
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
}
