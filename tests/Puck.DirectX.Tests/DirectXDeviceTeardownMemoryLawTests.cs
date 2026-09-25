using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
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
        var context = WarpDevices.Context(memory: memory);
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
        var context = WarpDevices.Context(memory: memory);
        var leaked = new DirectXGpuBufferFactory(deviceContext: context).CreateDeviceLocal(
            sizeBytes: LeakedBytes,
            usage: GpuBufferUsage.Storage
        );

        try {
            var refusal = Assert.Throws<InvalidOperationException>(testCode: () => context.Recreate());

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
        var context = WarpDevices.Context(memory: memory);

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
}
