using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>A Direct3D 12 context's teardown ends its device's memory entries, as a Vulkan logical device's does: a
/// device-local allocation still held when <see cref="DirectXDeviceContext.Dispose"/> or
/// <see cref="DirectXDeviceContext.Recreate"/> releases the device refuses the teardown by name, the device is released
/// regardless, and a teardown after every owner released its allocation refuses nothing. Each law runs on a software
/// (WARP) device without the debug layer and skips when the host has none that meets the device floor.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXDeviceTeardownMemoryLawTests {
    private const ulong LeakedBytes = 65536;

    [Fact]
    public void ADisposeHoldingACountedAllocationNamesItAndStillReleasesTheDevice() {
        var memory = new GpuDeviceMemoryWork(backend: "directx");
        var context = WarpContext(memory: memory);
        var device = context.Device.Handle;
        var leaked = new DirectXGpuBufferFactory(deviceContext: context).CreateDeviceLocal(
            sizeBytes: LeakedBytes,
            usage: GpuBufferUsage.Storage
        );

        try {
            var refusal = Assert.Throws<InvalidOperationException>(testCode: context.Dispose);

            Assert.StartsWith(
                actualString: refusal.Message,
                expectedStartString: $"memory.directx: device 0x{device:X} was torn down holding 1 counted allocation(s) their owners never released: 0x{leaked.BufferHandle:X} ("
            );
            Assert.False(condition: context.IsInitialized);
        } finally {
            leaked.Dispose();
        }

        Assert.Equal(
            actual: memory.Read(kind: GpuDeviceMemoryWork.Released),
            expected: 0L
        );
    }
    [Fact]
    public void ARecreateHoldingACountedAllocationNamesItAndCreatesNoDevice() {
        var memory = new GpuDeviceMemoryWork(backend: "directx");
        var context = WarpContext(memory: memory);
        var leaked = new DirectXGpuBufferFactory(deviceContext: context).CreateDeviceLocal(
            sizeBytes: LeakedBytes,
            usage: GpuBufferUsage.Storage
        );

        try {
            var refusal = Assert.Throws<InvalidOperationException>(testCode: context.Recreate);

            Assert.Contains(
                actualString: refusal.Message,
                expectedSubstring: "was torn down holding 1 counted allocation(s)"
            );
            Assert.False(condition: context.IsInitialized);
        } finally {
            leaked.Dispose();
            context.Dispose();
        }
    }
    [Fact]
    public void ATeardownAfterEveryOwnerReleasedRefusesNothing() {
        var memory = new GpuDeviceMemoryWork(backend: "directx");
        var context = WarpContext(memory: memory);

        new DirectXGpuBufferFactory(deviceContext: context).CreateDeviceLocal(
            sizeBytes: LeakedBytes,
            usage: GpuBufferUsage.Storage
        ).Dispose();
        context.Recreate();
        new DirectXGpuBufferFactory(deviceContext: context).CreateDeviceLocal(
            sizeBytes: LeakedBytes,
            usage: GpuBufferUsage.Storage
        ).Dispose();
        context.Dispose();

        Assert.Equal(
            actual: (memory.Held, (memory.Read(kind: GpuDeviceMemoryWork.Allocated) == memory.Read(kind: GpuDeviceMemoryWork.Released))),
            expected: (0L, true)
        );
    }

    // A context whose device is a software device, created; skips when the host has none that meets the floor.
    private static DirectXDeviceContext WarpContext(GpuDeviceMemoryWork memory) {
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

    // Creates every device on the software renderer and reads it through the native API.
    private sealed class WarpDeviceApi : IDirectXDeviceApi {
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
        public int GetDeviceRemovedReason(nint deviceHandle) =>
            m_native.GetDeviceRemovedReason(deviceHandle: deviceHandle);
        public GpuMemoryProfile GetMemoryProfile(nint deviceHandle) =>
            m_native.GetMemoryProfile(deviceHandle: deviceHandle);
        public DirectXFeatureLevel? ProbeMaxFeatureLevel(long adapterLuid) =>
            m_native.ProbeMaxFeatureLevel(adapterLuid: adapterLuid);
    }
}
