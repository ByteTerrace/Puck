using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuDeviceMemoryWork"/>: allocations and releases count at the size each allocation was counted
/// with, the peak is the most bytes held at once, a release of an object never counted counts nothing, an allocation is
/// counted once, and the class legend makes the byte counts per-backend-deterministic and the peak pacing.
/// </summary>
public sealed class GpuDeviceMemoryWorkLawTests {
    private static long Read(GpuDeviceMemoryWork work, WorkKind kind) {
        Assert.True(condition: work.TryRead(
            kind: kind,
            value: out var value
        ));

        return value;
    }

    [Fact]
    public void AllocationsAndReleasesCountTheirSizesAndThePeakHeld() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        work.CountAllocated(allocation: 0x10, bytes: 4096L);
        work.CountAllocated(allocation: 0x20, bytes: 1024L);
        Assert.True(condition: work.CountReleased(allocation: 0x10));
        work.CountAllocated(allocation: 0x30, bytes: 2048L);
        Assert.True(condition: work.CountReleased(allocation: 0x20));

        Assert.Equal(expected: "memory.vulkan", actual: work.Name);
        Assert.Equal(expected: 7168L, actual: Read(kind: GpuDeviceMemoryWork.Allocated, work: work));
        Assert.Equal(expected: 5120L, actual: Read(kind: GpuDeviceMemoryWork.Released, work: work));
        Assert.Equal(expected: 5120L, actual: Read(kind: GpuDeviceMemoryWork.Peak, work: work));
        Assert.Equal(expected: 2048L, actual: work.Held);
    }
    [Fact]
    public void AnUncountedReleaseCountsNothing() {
        var work = new GpuDeviceMemoryWork(backend: "directx");

        work.CountAllocated(allocation: 0x10, bytes: 64L);

        Assert.False(condition: work.CountReleased(allocation: 0));
        Assert.False(condition: work.CountReleased(allocation: 0x99));
        Assert.True(condition: work.CountReleased(allocation: 0x10));
        Assert.False(condition: work.CountReleased(allocation: 0x10));
        Assert.Equal(expected: 64L, actual: Read(kind: GpuDeviceMemoryWork.Released, work: work));
    }
    [Fact]
    public void AnAllocationIsCountedOnce() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        work.CountAllocated(allocation: 0x10, bytes: 64L);

        _ = Assert.Throws<ArgumentException>(testCode: () => work.CountAllocated(allocation: 0x10, bytes: 64L));
        _ = Assert.Throws<ArgumentException>(testCode: () => work.CountAllocated(allocation: 0, bytes: 64L));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => work.CountAllocated(allocation: 0x20, bytes: -1L));
        Assert.Equal(expected: 64L, actual: Read(kind: GpuDeviceMemoryWork.Allocated, work: work));
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
