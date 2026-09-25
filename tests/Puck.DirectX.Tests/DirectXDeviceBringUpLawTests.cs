using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>The lazily created Direct3D 12 device: a creation this host cannot satisfy is classified at the source as
/// <see cref="GpuDeviceUnavailableException"/>, and draining a device that was never created returns at once without
/// attempting the creation, so teardown after a failed bring-up neither retries it nor masks it. The device's identity is read only
/// from a created device, so it is absent before and after a failed bring-up. A bring-up that fails after its device is
/// created releases that device and leaves the context retryable: the next use creates a device again rather than
/// finding one with no queue. The device API is a fake whose creation fails, touching no adapter, or one that creates a
/// software (WARP) device and then fails to read it.</summary>
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
    [Fact]
    public void ABringUpThatFailsAfterCreatingItsDeviceReleasesItAndStaysRetryable() {
        var api = new UnreadableDeviceApi();
        using var context = new DirectXDeviceContext(
            adapterLuid: 7L,
            deviceApi: api,
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        );

        _ = Assert.Throws<ArgumentException>(testCode: () => context.DeviceHandle);

        if (api.Created.Count == 0) {
            Assert.Skip(reason: "no Direct3D 12 software device on this host");
        }

        Assert.False(condition: context.IsInitialized);
        Assert.Null(@object: context.Identity);
        Assert.Null(@object: context.Capabilities);
        Assert.Equal(
            actual: api.Created[0].Handle,
            expected: 0
        );

        // The next use starts the bring-up again, and releases what that attempt created too.
        _ = Assert.Throws<ArgumentException>(testCode: () => context.CommandQueueHandle);
        Assert.Equal(
            actual: api.Created.Count,
            expected: 2
        );
        Assert.Equal(
            actual: api.Created[1].Handle,
            expected: 0
        );
        Assert.False(condition: context.IsInitialized);
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
    // Creates a real software device, then refuses to read it the way a capability probe refused by an older runtime
    // surfaces through CsWin32's throwing wrappers.
    private sealed class UnreadableDeviceApi : IDirectXDeviceApi {
        private readonly DirectXNativeDeviceApi m_native = new();

        public List<DirectXDevice> Created { get; } = [];

        public DirectXDevice CreateDevice(long adapterLuid, DirectXFeatureLevel minimumFeatureLevel) {
            DirectXDevice device;

            try {
                device = m_native.CreateWarpDevice(minimumFeatureLevel: minimumFeatureLevel);
            } catch (DirectXException) {
                throw new ArgumentException(message: "no software device");
            }

            Created.Add(item: device);

            return device;
        }
        public DirectXDevice CreateWarpDevice(DirectXFeatureLevel minimumFeatureLevel) => throw new NotSupportedException();
        public long GetAdapterLuid(nint deviceHandle) => throw new NotSupportedException();
        public GpuDeviceIdentity GetDeviceIdentity(nint deviceHandle) => throw new ArgumentException(message: "Value does not fall within the expected range.");
        public int GetDeviceRemovedReason(nint deviceHandle) => 0;
        public GpuMemoryProfile GetMemoryProfile(nint deviceHandle) => throw new NotSupportedException();
        public GpuDeviceCapabilities GetDeviceCapabilities(nint deviceHandle) => throw new NotSupportedException();
        public DirectXFeatureLevel? ProbeMaxFeatureLevel(long adapterLuid) => throw new NotSupportedException();
    }
}
