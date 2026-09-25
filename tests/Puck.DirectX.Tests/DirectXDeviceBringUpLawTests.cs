using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>The lazily created Direct3D 12 device: a creation this host cannot satisfy is classified at the source as
/// <see cref="GpuDeviceUnavailableException"/>, and draining a device that was never created returns at once without
/// attempting the creation, so teardown after a failed bring-up neither retries it nor masks it. The device's identity is read only
/// from a created device, so it is absent before and after a failed bring-up. The device API is a
/// fake whose creation fails; no adapter is touched.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXDeviceBringUpLawTests {
    [Fact]
    public void AFailedCreationIsAnUnavailableDevice() {
        var api = new FailingDeviceApi();
        var context = new DirectXDeviceContext(
            adapterLuid: 7L,
            deviceApi: api,
            minimumFeatureLevel: DirectXFeatureLevel.Level120
        );

        var unavailable = Assert.Throws<GpuDeviceUnavailableException>(testCode: () => context.DeviceHandle);

        Assert.Equal(
            expected: "directx",
            actual: unavailable.Backend
        );
        Assert.IsType<DirectXException>(@object: unavailable.InnerException);
        Assert.False(condition: context.IsInitialized);
    }
    [Fact]
    public void DrainingADeviceThatWasNeverCreatedDoesNotCreateIt() {
        var api = new FailingDeviceApi();
        var context = new DirectXDeviceContext(
            adapterLuid: 7L,
            deviceApi: api,
            minimumFeatureLevel: DirectXFeatureLevel.Level120
        );

        context.WaitIdle();
        context.TryWaitIdle();
        context.Dispose();

        Assert.Equal(
            expected: 0,
            actual: api.CreateDeviceCalls
        );
    }
    [Fact]
    public void TheIdentityIsAbsentUntilADeviceIsCreatedAndReadingItCreatesNone() {
        var api = new FailingDeviceApi();
        var context = new DirectXDeviceContext(
            adapterLuid: 7L,
            deviceApi: api,
            minimumFeatureLevel: DirectXFeatureLevel.Level120
        );

        Assert.Null(@object: context.Identity);
        Assert.Equal(
            expected: 0,
            actual: api.CreateDeviceCalls
        );
        _ = Assert.Throws<GpuDeviceUnavailableException>(testCode: () => context.DeviceHandle);
        Assert.Null(@object: context.Identity);
        Assert.Equal(
            expected: 0,
            actual: api.GetDeviceIdentityCalls
        );
    }

    private sealed class FailingDeviceApi : IDirectXDeviceApi {
        public int CreateDeviceCalls { get; private set; }
        public int GetDeviceIdentityCalls { get; private set; }

        public DirectXDevice CreateDevice(long adapterLuid, DirectXFeatureLevel minimumFeatureLevel) {
            CreateDeviceCalls++;

            throw new DirectXException(
                operation: "D3D12CreateDevice",
                result: unchecked((int)0x887A0004)
            );
        }
        public DirectXDevice CreateWarpDevice(DirectXFeatureLevel minimumFeatureLevel) => throw new NotSupportedException();
        public long GetAdapterLuid(nint deviceHandle) => throw new NotSupportedException();
        public GpuDeviceIdentity GetDeviceIdentity(nint deviceHandle) {
            GetDeviceIdentityCalls++;

            throw new NotSupportedException();
        }
        public int GetDeviceRemovedReason(nint deviceHandle) => throw new NotSupportedException();
        public GpuMemoryProfile GetMemoryProfile(nint deviceHandle) => throw new NotSupportedException();
        public GpuDeviceCapabilities GetDeviceCapabilities(nint deviceHandle) => throw new NotSupportedException();
        public DirectXFeatureLevel? ProbeMaxFeatureLevel(long adapterLuid) => throw new NotSupportedException();
    }
}
