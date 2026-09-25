using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuDeviceMemoryWork"/>: allocations and releases count at the size each allocation was counted
/// with, the peak is the most bytes held at once, only the device-local role counts, a release of an object never
/// counted counts nothing, an allocation is counted once per device, the same handle on two devices is two entries, a
/// device's teardown names every allocation it still holds, and the class legend makes the byte counts
/// per-backend-deterministic and the peak pacing.
/// </summary>
public sealed class GpuDeviceMemoryWorkLawTests {
    private const nint Device = 0xD0;
    private const nint OtherDevice = 0xE0;

    private static long Read(GpuDeviceMemoryWork work, WorkKind kind) {
        Assert.True(condition: work.TryRead(
            kind: kind,
            value: out var value
        ));

        return value;
    }
    private static bool Allocate(GpuDeviceMemoryWork work, nint allocation, long bytes, nint device = Device, GpuMemoryRole role = GpuMemoryRole.DeviceLocal) =>
        work.CountAllocated(
            allocation: allocation,
            bytes: bytes,
            device: device,
            role: role
        );

    [Fact]
    public void AllocationsAndReleasesCountTheirSizesAndThePeakHeld() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        Assert.True(condition: Allocate(work: work, allocation: 0x10, bytes: 4096L));
        Assert.True(condition: Allocate(work: work, allocation: 0x20, bytes: 1024L));
        Assert.True(condition: work.CountReleased(allocation: 0x10, device: Device));
        Assert.True(condition: Allocate(work: work, allocation: 0x30, bytes: 2048L));
        Assert.True(condition: work.CountReleased(allocation: 0x20, device: Device));

        Assert.Equal(expected: "memory.vulkan", actual: work.Name);
        Assert.Equal(expected: 7168L, actual: Read(kind: GpuDeviceMemoryWork.Allocated, work: work));
        Assert.Equal(expected: 5120L, actual: Read(kind: GpuDeviceMemoryWork.Released, work: work));
        Assert.Equal(expected: 5120L, actual: Read(kind: GpuDeviceMemoryWork.Peak, work: work));
        Assert.Equal(expected: 2048L, actual: work.Held);
    }
    [Fact]
    public void OnlyTheDeviceLocalRoleCounts() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        Assert.True(condition: GpuDeviceMemoryWork.IsCounted(role: GpuMemoryRole.DeviceLocal));
        Assert.False(condition: GpuDeviceMemoryWork.IsCounted(role: GpuMemoryRole.HostVisible));
        Assert.False(condition: Allocate(work: work, allocation: 0x10, bytes: 4096L, role: GpuMemoryRole.HostVisible));
        Assert.False(condition: work.CountReleased(allocation: 0x10, device: Device));
        Assert.Equal(expected: (0L, 0L), actual: (Read(kind: GpuDeviceMemoryWork.Allocated, work: work), work.Held));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuDeviceMemoryWork.IsCounted(role: ((GpuMemoryRole)7)));
    }
    [Fact]
    public void AnUncountedReleaseCountsNothing() {
        var work = new GpuDeviceMemoryWork(backend: "directx");

        _ = Allocate(work: work, allocation: 0x10, bytes: 64L);

        Assert.False(condition: work.CountReleased(allocation: 0, device: Device));
        Assert.False(condition: work.CountReleased(allocation: 0x99, device: Device));
        Assert.False(condition: work.CountReleased(allocation: 0x10, device: OtherDevice));
        Assert.True(condition: work.CountReleased(allocation: 0x10, device: Device));
        Assert.False(condition: work.CountReleased(allocation: 0x10, device: Device));
        Assert.Equal(expected: 64L, actual: Read(kind: GpuDeviceMemoryWork.Released, work: work));
    }
    [Fact]
    public void AnAllocationIsCountedOncePerDevice() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        _ = Allocate(work: work, allocation: 0x10, bytes: 64L);

        _ = Assert.Throws<ArgumentException>(testCode: () => Allocate(work: work, allocation: 0x10, bytes: 64L));
        _ = Assert.Throws<ArgumentException>(testCode: () => Allocate(work: work, allocation: 0, bytes: 64L));
        _ = Assert.Throws<ArgumentException>(testCode: () => Allocate(work: work, allocation: 0x20, bytes: 64L, device: 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Allocate(work: work, allocation: 0x20, bytes: -1L));
        Assert.Equal(expected: 64L, actual: Read(kind: GpuDeviceMemoryWork.Allocated, work: work));
    }
    [Fact]
    public void AHandleReusedByARecreatedDeviceIsItsOwnEntry() {
        var work = new GpuDeviceMemoryWork(backend: "directx");

        Assert.True(condition: Allocate(work: work, allocation: 0x10, bytes: 64L));
        Assert.True(condition: Allocate(work: work, allocation: 0x10, bytes: 32L, device: OtherDevice));
        Assert.True(condition: work.CountReleased(allocation: 0x10, device: OtherDevice));

        Assert.Equal(expected: (96L, 32L, 64L), actual: (Read(kind: GpuDeviceMemoryWork.Allocated, work: work), Read(kind: GpuDeviceMemoryWork.Released, work: work), work.Held));
    }
    [Fact]
    public void ATeardownHoldingAnAllocationNamesEveryLeakAndKeepsItHeld() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        _ = Allocate(work: work, allocation: 0x30, bytes: 2048L);
        _ = Allocate(work: work, allocation: 0x10, bytes: 4096L);
        _ = Allocate(work: work, allocation: 0x20, bytes: 1024L, device: OtherDevice);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => work.EndDevice(device: Device));

        Assert.Equal(
            actual: refusal.Message,
            expected: "memory.vulkan: device 0xD0 was torn down holding 2 counted allocation(s) their owners never released: 0x10 (4096 bytes), 0x30 (2048 bytes)."
        );
        Assert.Equal(expected: 7168L, actual: work.Held);
        Assert.False(condition: work.CountReleased(allocation: 0x10, device: Device));
        Assert.True(condition: Allocate(work: work, allocation: 0x10, bytes: 8L));

        work.EndDevice(device: (OtherDevice + 1));
    }
    [Fact]
    public void TheLegendMakesBytesPerBackendDeterministicAndThePeakPacing() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        Assert.Equal(
            expected: [
                ("gpu.memory.device-local.allocated", "bytes", WorkClass.PerBackendDeterministic),
                ("gpu.memory.device-local.released", "bytes", WorkClass.PerBackendDeterministic),
                ("gpu.memory.device-local.peak", "bytes", WorkClass.Pacing),
            ],
            actual: [.. work.WorkKinds.ToArray().Select(selector: static kind => (kind.Name, kind.Unit, kind.Class))]
        );
    }
}
