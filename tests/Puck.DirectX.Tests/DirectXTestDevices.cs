using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Testing;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>The devices the suite's device laws run on: the software (WARP) device without the debug layer, and the
/// default adapter with it.</summary>
[SupportedOSPlatform("windows10.0.10240")]
internal static class DirectXTestDevices {
    /// <summary>Returns a context on the default adapter with the debug layer on, its device created; skips the calling
    /// test when the host has no device or the debug layer does not load.</summary>
    /// <param name="output">The writer the context drains debug messages into.</param>
    /// <returns>The context, owned by the caller.</returns>
    internal static DirectXDeviceContext Debug(StringWriter output) {
        var context = new DirectXDeviceContext(
            adapterLuid: 0,
            deviceApi: new DirectXNativeDeviceApi(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        ) {
            DebugOutput = output,
            EnableDebugLayer = true,
        };

        try {
            _ = context.Device;
        } catch (GpuDeviceUnavailableException exception) {
            context.Dispose();
            Assert.Skip(reason: $"no Direct3D 12 device with the debug layer on this host: {exception.Message}");
        }

        if (!context.HasDebugLayer) {
            context.Dispose();
            Assert.Skip(reason: "the Direct3D 12 debug layer did not load on this host");
        }

        return context;
    }
    /// <summary>Returns a context whose device is a software device, created; skips the calling test when the host has
    /// none that meets the floor.</summary>
    /// <param name="memory">The memory counts the context records into, or <see langword="null"/> for none.</param>
    /// <returns>The context, owned by the caller.</returns>
    internal static DirectXDeviceContext Warp(GpuDeviceMemoryWork? memory = null) {
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
