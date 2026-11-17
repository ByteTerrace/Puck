using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuDeviceMemoryWork"/>: allocations and releases count at the size each allocation was counted
/// with, the peak is the most bytes held at once, the aperture rows count the host-mapped part, host-visible bytes count in
/// their own disjoint rows, every allocation counts once, a release of an object never
/// counted counts nothing, an allocation is counted once per device, the same handle on two devices is two entries, a
/// device's teardown names every allocation it still holds, and the class legend makes every count pacing.
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
    // Registers a discrete adapter's segments for the device on its first allocation, and places each allocation in the
    // segment its role names.
    private static void Allocate(GpuDeviceMemoryWork work, nint allocation, long bytes, nint device = Device, GpuMemoryRole role = GpuMemoryRole.DeviceLocal) {
        try {
            _ = work.SegmentsOf(device: device);
        } catch (InvalidOperationException) {
            work.BeginDevice(device: device, segments: GpuMemorySegments.FromDirectX(profile: default));
        }

        work.CountAllocated(
            allocation: allocation,
            grant: work.Admit(
                bytes: bytes,
                device: device,
                family: null,
                payer: null,
                payerBytes: 0L,
                role: role,
                segment: (GpuDeviceMemoryWork.IsDeviceLocal(role: role) ? GpuMemorySegments.DirectXLocal : GpuMemorySegments.DirectXNonLocal)
            )
        );
    }

    [Fact]
    public void AllocationsAndReleasesCountTheirSizesAndThePeakHeld() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        Allocate(work: work, allocation: 0x10, bytes: 4096L);
        Allocate(work: work, allocation: 0x20, bytes: 1024L);
        Assert.True(condition: work.CountReleased(allocation: 0x10, device: Device));
        Allocate(work: work, allocation: 0x30, bytes: 2048L);
        Assert.True(condition: work.CountReleased(allocation: 0x20, device: Device));

        Assert.Equal(expected: "memory.vulkan", actual: work.Name);
        Assert.Equal(expected: 7168L, actual: Read(kind: GpuDeviceMemoryWork.Allocated, work: work));
        Assert.Equal(expected: 5120L, actual: Read(kind: GpuDeviceMemoryWork.Released, work: work));
        Assert.Equal(expected: 5120L, actual: Read(kind: GpuDeviceMemoryWork.Peak, work: work));
        Assert.Equal(expected: 2048L, actual: work.Held);
    }
    [Fact]
    public void HostVisibleBytesCountInTheirOwnDisjointRows() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        Assert.True(condition: GpuDeviceMemoryWork.IsDeviceLocal(role: GpuMemoryRole.DeviceLocal));
        Assert.True(condition: GpuDeviceMemoryWork.IsDeviceLocal(role: GpuMemoryRole.HostVisibleDeviceLocal));
        Assert.False(condition: GpuDeviceMemoryWork.IsDeviceLocal(role: GpuMemoryRole.HostVisible));
        Allocate(work: work, allocation: 0x10, bytes: 4096L, role: GpuMemoryRole.HostVisible);
        Allocate(work: work, allocation: 0x20, bytes: 1024L);
        Allocate(work: work, allocation: 0x30, bytes: 512L, role: GpuMemoryRole.HostVisible);
        Assert.True(condition: work.CountReleased(allocation: 0x10, device: Device));

        Assert.Equal(expected: (1024L, 512L), actual: (work.Held, work.HostVisibleHeld));
        Assert.Equal(expected: (1024L, 0L), actual: (Read(kind: GpuDeviceMemoryWork.Allocated, work: work), Read(kind: GpuDeviceMemoryWork.Released, work: work)));
        Assert.Equal(expected: (4608L, 4096L, 4608L), actual: (Read(kind: GpuDeviceMemoryWork.HostVisibleAllocated, work: work), Read(kind: GpuDeviceMemoryWork.HostVisibleReleased, work: work), Read(kind: GpuDeviceMemoryWork.HostVisiblePeak, work: work)));
        Assert.Equal(expected: 3L, actual: Read(kind: GpuDeviceMemoryWork.Allocations, work: work));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuDeviceMemoryWork.IsDeviceLocal(role: ((GpuMemoryRole)7)));
    }
    // The aperture rows are the part of the device-local rows the host maps: a small heap on a discrete adapter, which each
    // residency's ring tables live in.
    [Fact]
    public void TheApertureRowsCountThePartOfDeviceLocalMemoryTheHostMaps() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        Allocate(work: work, allocation: 0x10, bytes: 4096L);
        Allocate(work: work, allocation: 0x20, bytes: 1024L, role: GpuMemoryRole.HostVisibleDeviceLocal);
        Allocate(work: work, allocation: 0x30, bytes: 512L, role: GpuMemoryRole.HostVisibleDeviceLocal);
        Assert.True(condition: work.CountReleased(allocation: 0x20, device: Device));
        Allocate(work: work, allocation: 0x40, bytes: 256L, role: GpuMemoryRole.HostVisibleDeviceLocal);
        Assert.True(condition: work.CountReleased(allocation: 0x10, device: Device));

        Assert.Equal(expected: 5888L, actual: Read(kind: GpuDeviceMemoryWork.Allocated, work: work));
        Assert.Equal(expected: 5120L, actual: Read(kind: GpuDeviceMemoryWork.Released, work: work));
        Assert.Equal(expected: 1792L, actual: Read(kind: GpuDeviceMemoryWork.ApertureAllocated, work: work));
        Assert.Equal(expected: 1024L, actual: Read(kind: GpuDeviceMemoryWork.ApertureReleased, work: work));
        Assert.Equal(expected: 1536L, actual: Read(kind: GpuDeviceMemoryWork.AperturePeak, work: work));
    }
    [Fact]
    public void AnUncountedReleaseCountsNothing() {
        var work = new GpuDeviceMemoryWork(backend: "directx");

        Allocate(work: work, allocation: 0x10, bytes: 64L);

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

        Allocate(work: work, allocation: 0x10, bytes: 64L);

        _ = Assert.Throws<ArgumentException>(testCode: () => Allocate(work: work, allocation: 0x10, bytes: 64L));
        _ = Assert.Throws<ArgumentException>(testCode: () => Allocate(work: work, allocation: 0, bytes: 64L));
        _ = Assert.Throws<ArgumentException>(testCode: () => Allocate(work: work, allocation: 0x20, bytes: 64L, device: 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Allocate(work: work, allocation: 0x20, bytes: -1L));
        Assert.Equal(expected: 64L, actual: Read(kind: GpuDeviceMemoryWork.Allocated, work: work));
    }
    [Fact]
    public void AHandleReusedByARecreatedDeviceIsItsOwnEntry() {
        var work = new GpuDeviceMemoryWork(backend: "directx");

        Allocate(work: work, allocation: 0x10, bytes: 64L);
        Allocate(work: work, allocation: 0x10, bytes: 32L, device: OtherDevice);
        Assert.True(condition: work.CountReleased(allocation: 0x10, device: OtherDevice));

        Assert.Equal(expected: (96L, 32L, 64L), actual: (Read(kind: GpuDeviceMemoryWork.Allocated, work: work), Read(kind: GpuDeviceMemoryWork.Released, work: work), work.Held));
    }
    [Fact]
    public void ATeardownHoldingAnAllocationNamesEveryLeakAndKeepsItHeld() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        Allocate(work: work, allocation: 0x30, bytes: 2048L);
        Allocate(work: work, allocation: 0x10, bytes: 4096L);
        Allocate(work: work, allocation: 0x20, bytes: 1024L, device: OtherDevice);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => work.EndDevice(device: Device));

        Assert.Equal(
            actual: refusal.Message,
            expected: "memory.vulkan: device 0xD0 was torn down holding 2 counted allocation(s) their owners never released: 0x10 (4096 bytes), 0x30 (2048 bytes)."
        );
        Assert.Equal(expected: 7168L, actual: work.Held);
        Assert.False(condition: work.CountReleased(allocation: 0x10, device: Device));
        Allocate(work: work, allocation: 0x10, bytes: 8L);

        work.EndDevice(device: (OtherDevice + 1));
    }
    [Fact]
    public void FamiliesNameEachCountedByteOnceAndNeverPastItsBlock() {
        var work = new GpuDeviceMemoryWork(backend: "directx");

        Allocate(work: work, allocation: 0x10, bytes: 1000L);
        var first = work.CountPlaced(allocation: 0x10, bytes: 600L, device: Device, family: "sdf.world/mesh-region");
        var second = work.CountPlaced(allocation: 0x10, bytes: 300L, device: Device, family: "view/targets");

        _ = Assert.Throws<InvalidOperationException>(testCode: () => work.CountPlaced(allocation: 0x10, bytes: 200L, device: Device, family: "view/targets"));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => work.CountPlaced(allocation: 0x99, bytes: 1L, device: Device, family: "view/targets"));
        work.CountAllocated(allocation: 0x20, grant: work.Admit(bytes: 500L, device: Device, family: "backend/descriptor-heaps", payer: "backend/descriptor-heaps", payerBytes: 500L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => work.CountPlaced(allocation: 0x20, bytes: 1L, device: Device, family: "view/targets"));

        var local = work.ReadSegments().Single(predicate: static row => (row.Segment.Index == GpuMemorySegments.DirectXLocal));

        Assert.Equal(expected: (1500L, 1400L, 100L), actual: (local.Held, local.Named, local.Residual));
        Assert.True(condition: work.CountUnplaced(token: second));
        Assert.False(condition: work.CountUnplaced(token: second));
        // A block freed with a placement still named in it takes the name with it.
        Assert.True(condition: work.CountReleased(allocation: 0x10, device: Device));
        Assert.False(condition: work.CountUnplaced(token: first));

        var families = work.ReadFamilies().ToDictionary(keySelector: static row => row.Family);

        Assert.Equal(expected: (0L, 600L, 600L), actual: (families["sdf.world/mesh-region"].Held, families["sdf.world/mesh-region"].Allocated, families["sdf.world/mesh-region"].Released));
        Assert.Equal(expected: (0L, 300L), actual: (families["view/targets"].Held, families["view/targets"].Peak));
        Assert.Equal(expected: 500L, actual: families["backend/descriptor-heaps"].Held);
    }
    [Fact]
    public void AnAllocationIsCountedOnlyUnderAGrantAndAGrantSettlesOnce() {
        var owners = new RefusingOwners();
        var work = new GpuDeviceMemoryWork(backend: "vulkan", owners: owners);

        work.BeginDevice(device: Device, segments: GpuMemorySegments.FromDirectX(profile: default));
        // The account's refusal is the door's: nothing is granted, so nothing can be counted.
        owners.Verdict = GpuMemoryVerdict.Refused;
        Assert.False(condition: work.TryAdmit(bytes: 4096L, device: Device, family: null, grant: out var refused, payer: "sdf.mesh-region/mesh-region", payerBytes: 1024L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        Assert.Null(@object: refused);
        _ = Assert.Throws<GpuMemoryRefusedException>(testCode: () => work.Admit(bytes: 4096L, device: Device, family: null, payer: null, payerBytes: 0L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        Assert.Equal(expected: 2L, actual: Read(kind: GpuDeviceMemoryWork.AdmissionRefusals, work: work));
        // A grant carries what is counted and settles once: counted, returned or refused by the device.
        owners.Verdict = GpuMemoryVerdict.Granted;

        var counted = work.Admit(bytes: 4096L, device: Device, family: null, payer: "sdf.mesh-region/mesh-region", payerBytes: 1024L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal);

        work.CountAllocated(allocation: 0x10, grant: counted);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => work.CountAllocated(allocation: 0x11, grant: counted));
        _ = Assert.Throws<InvalidOperationException>(testCode: counted.Return);

        var returned = work.Admit(bytes: 100L, device: Device, family: null, payer: null, payerBytes: 0L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal);

        returned.Return();

        var device = work.Admit(bytes: 200L, device: Device, family: null, payer: null, payerBytes: 0L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal);

        Assert.True(condition: device.Refuse().ByDevice);
        Assert.Equal(expected: (4096L, 1L, 1L), actual: (work.Held, work.DeviceRefusals, Read(kind: GpuDeviceMemoryWork.DeviceRefusalCount, work: work)));
        Assert.Equal(
            expected: [("admit", 4096L, 1024L), ("admit", 4096L, 0L), ("admit", 4096L, 1024L), ("Counted", 4096L, 0L), ("admit", 100L, 0L), ("Returned", 100L, 0L), ("admit", 200L, 0L), ("DeviceRefused", 200L, 0L)],
            actual: owners.Log
        );
        // An allocation made inside the scope is asked as one that gives way, on this thread only, and the scope ends.
        using (GpuMemoryAdmissionScope.Enter()) {
            work.Admit(bytes: 300L, device: Device, family: null, payer: null, payerBytes: 0L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal).Return();
        }
        work.Admit(bytes: 300L, device: Device, family: null, payer: null, payerBytes: 0L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal).Return();
        Assert.Equal(expected: [("admit-gives-way", 300L, 0L), ("Returned", 300L, 0L), ("admit", 300L, 0L), ("Returned", 300L, 0L)], actual: owners.Log[^4..]);
        // Another ledger's grant is refused.
        _ = Assert.Throws<ArgumentException>(testCode: () => new GpuDeviceMemoryWork(backend: "directx").CountAllocated(allocation: 0x12, grant: counted));
    }

    // An account that answers every admission with one verdict and logs what it was asked and told.
    private sealed class RefusingOwners : IGpuMemoryOwners {
        public event Action? TrimRequested { add { } remove { } }

        public List<(string Call, long Bytes, long PayerBytes)> Log { get; } = [];
        public GpuMemoryVerdict Verdict { get; set; } = GpuMemoryVerdict.Granted;

        public GpuMemoryAdmission Admit(GpuMemorySegment segment, long bytes, string? payer, long payerBytes, bool givesWay) {
            Log.Add(item: ((givesWay ? "admit-gives-way" : "admit"), bytes, payerBytes));

            return new(Bytes: bytes, Net: bytes, Payer: null, Verdict: Verdict);
        }
        public void Settle(GpuMemorySegment segment, GpuMemoryAdmission admission, GpuMemorySettlement settlement) => Log.Add(item: (settlement.ToString(), admission.Bytes, 0L));
        public Puck.Abstractions.Memory.IAllocationObserver OfSegment(GpuMemorySegment segment) => new Observer();
        public Puck.Abstractions.Memory.IAllocationObserver OfFamily(GpuMemorySegment segment, string family) => new Observer();

        private sealed class Observer : Puck.Abstractions.Memory.IAllocationObserver {
            public void Change(long bytes, Puck.Abstractions.Memory.MemoryBacking backing) { }
        }
    }

    [Fact]
    public void TheLegendMakesEveryCountPacing() {
        var work = new GpuDeviceMemoryWork(backend: "vulkan");

        Assert.Equal(
            expected: [
                ("gpu.memory.device-local.allocated", "bytes", WorkClass.Pacing),
                ("gpu.memory.device-local.released", "bytes", WorkClass.Pacing),
                ("gpu.memory.device-local.peak", "bytes", WorkClass.Pacing),
                ("gpu.memory.host-visible-device-local.allocated", "bytes", WorkClass.Pacing),
                ("gpu.memory.host-visible-device-local.released", "bytes", WorkClass.Pacing),
                ("gpu.memory.host-visible-device-local.peak", "bytes", WorkClass.Pacing),
                ("gpu.memory.host-visible.allocated", "bytes", WorkClass.Pacing),
                ("gpu.memory.host-visible.released", "bytes", WorkClass.Pacing),
                ("gpu.memory.host-visible.peak", "bytes", WorkClass.Pacing),
                ("gpu.memory.allocations", "count", WorkClass.Pacing),
                ("gpu.memory.placement-disagreements", "count", WorkClass.Pacing),
                ("gpu.memory.admission-refusals", "count", WorkClass.Pacing),
                ("gpu.memory.over-limit-grants", "count", WorkClass.Pacing),
                ("gpu.memory.device-refusals", "count", WorkClass.Pacing),
            ],
            actual: [.. work.WorkKinds.ToArray().Select(selector: static kind => (kind.Name, kind.Unit, kind.Class))]
        );
    }
}
