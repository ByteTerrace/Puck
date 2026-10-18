using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Every Direct3D 12 host rebuilds a lost device under one retry rule
/// (<see cref="Interop.DirectXDeviceContext.Recreate"/>): a rebuild that fails in Direct3D 12, whether the device's
/// creation or the host's re-initialization of its own objects on the new device, has not got its device back yet and
/// answers <see cref="DeviceLostException"/>, so the recovery waits and calls again; any other failure ends the
/// recovery. Each law runs on a software (WARP) device and skips when the host has none.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXDeviceRecreateLawTests {
    private const int NotCurrentlyAvailable = unchecked((int)0x887A0022);

    [Fact]
    public void AReinitializationThatFailsInDirect3DIsALossTheNextRecreateRetries() {
        using var context = DirectXTestDevices.Warp();
        var calls = 0;

        var loss = Assert.Throws<DeviceLostException>(testCode: () => context.Recreate(reinitialize: () => {
            calls++;

            throw new DirectXException(
                operation: "IDXGIFactory2::CreateSwapChainForHwnd",
                result: NotCurrentlyAvailable
            );
        }));

        Assert.Equal(
            actual: loss.ReasonCode,
            expected: NotCurrentlyAvailable
        );
        Assert.Contains(
            actualString: loss.Message,
            expectedSubstring: "IDXGIFactory2::CreateSwapChainForHwnd"
        );
        Assert.IsType<DirectXException>(@object: loss.InnerException);

        context.Recreate(reinitialize: () => calls++);

        Assert.Equal(
            actual: calls,
            expected: 2
        );
        Assert.True(condition: context.IsInitialized);
    }
    /// <summary>A device removed on purpose (<c>gpu.faults remove</c>) reports a removal, and recreating it writes the
    /// removed device's DRED record once, naming the reason, before the new device stands; a context with no device
    /// removes nothing.</summary>
    [Fact]
    public void ARemovedDeviceWritesItsRemovalRecordOnceAsItIsRecreated() {
        var output = new StringWriter();
        using var context = DirectXTestDevices.Warp(debugOutput: output);

        Assert.True(condition: context.RemoveDevice());
        context.Recreate();

        var lines = output.ToString().Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.StartsWith(expectedStartString: "[d3d12-dred] device removed, reason 0x887A0005;", actualString: Assert.Single(collection: lines, predicate: static line => line.Contains(value: "device removed", comparisonType: StringComparison.Ordinal)));
        Assert.All(collection: lines, action: static line => Assert.StartsWith(expectedStartString: DirectXRemovalReport.LinePrefix, actualString: line));
        Assert.True(condition: context.IsInitialized);
        context.Recreate();
        Assert.Equal(actual: output.ToString().Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries).Length, expected: lines.Length);

        using var idle = new Interop.DirectXDeviceContext(adapterLuid: 1L, deviceApi: new Apis.DirectXWarpDeviceApi(), minimumFeatureLevel: DirectXFeatureLevel.Level110);

        Assert.False(condition: idle.RemoveDevice());
    }
    [Fact]
    public void AnyOtherReinitializationFailureEndsTheRecovery() {
        using var context = DirectXTestDevices.Warp();

        _ = Assert.Throws<InvalidOperationException>(testCode: () => context.Recreate(reinitialize: static () => throw new InvalidOperationException(message: "not a Direct3D 12 failure")));
    }
}
