using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for the account's admission (<see cref="MemoryAccount"/>): what a pool holds and has promised stays under its
/// limit through every admission, apart from the counted exceptions; a device allocation is admitted before it is
/// counted and a reservation it consumes adds nothing; a growth reserves the old and the new buffer together; the
/// device's own refusal lowers its segment's limit; one forced job is admitted at a time; and a limit that falls under
/// what a pool holds fires one trim.
/// </summary>
public sealed class MemoryAdmissionLawTests {
    private const nint Device = 0xD0;
    private const long Gibibyte = (1L << 30);
    private const long Mebibyte = (1L << 20);
    private const string RegionFamily = "sdf.mesh-region/mesh-region";
    private const string RegionOwner = "sdf.mesh-region";

    // A discrete adapter's ledger reporting to an account that reads no free memory, so the host pool is its budget.
    private static (MemoryAccount Account, GpuDeviceMemoryWork Ledger, GpuMemorySegments Segments) Discrete(long installedBytes = (64L * Gibibyte)) {
        var account = new MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: installedBytes);
        var ledger = new GpuDeviceMemoryWork(backend: "law", owners: account.DeviceOwners(backend: "law"));
        var segments = GpuMemorySegments.FromDirectX(profile: default);

        ledger.BeginDevice(device: Device, segments: segments);

        return (account, ledger, segments);
    }
    // Records a reading whose local segment group has a budget; the spend limit is the budget less the larger of a tenth
    // of it and the headroom.
    private static void Budget(MemoryAccount account, GpuMemorySegments segments, long localBudget, ulong serial) =>
        account.Record(reading: GpuMemoryReading.Read(
            estimated: false,
            readings: [
                new GpuSegmentReading(AvailableForReservation: null, Budget: ((ulong)localBudget), CurrentReservation: null, Segment: segments[GpuMemorySegments.DirectXLocal], Usage: null),
                new GpuSegmentReading(AvailableForReservation: null, Budget: ((ulong)(64L * Gibibyte)), CurrentReservation: null, Segment: segments[GpuMemorySegments.DirectXNonLocal], Usage: null),
            ],
            segments: segments,
            serial: serial
        ));
    // Allocates a dedicated device-local allocation for a family, or reports the refusal.
    private static bool Allocate(GpuDeviceMemoryWork ledger, nint allocation, long bytes, string family) {
        if (!ledger.TryAdmit(bytes: bytes, device: Device, family: family, grant: out var grant, payer: family, payerBytes: bytes, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal)) {
            return false;
        }

        ledger.CountAllocated(allocation: allocation, grant: grant);

        return true;
    }

    [Fact]
    public void HeldPlusPromisedNeverPassesTheLimitAsTheDeviceBudgetIsLoweredInSteps() {
        var (account, ledger, segments) = Discrete();
        using var region = account.ReserveDevice(memoryClass: MemoryClass.Elastic, owner: RegionOwner, pool: PhysicalMemory.Device);
        var next = ((nint)0x1000);
        var regionBuffer = ((nint)0);
        var capacity = 0L;
        var refusals = 0;
        var grown = 0;
        var serial = 0UL;

        // The fixed path: the device's budget falls step by step while the region tries to grow by half a gibibyte and a
        // view target of sixty-four mebibytes is created at every step. Nothing throws at any step.
        foreach (var budget in new[] { 8L, 8L, 6L, 6L, 4L, 4L, 3L, 2L, 2L, 1L, 1L }) {
            Budget(account: account, localBudget: (budget * Gibibyte), segments: segments, serial: ++serial);

            var before = account.ReadAdmission(pool: PhysicalMemory.Device);
            var replacement = (capacity + (Gibibyte / 2L));

            // A growth by copy reserves the standing buffer and its replacement together.
            if (region.TryReserve(peak: (capacity + replacement))) {
                var reserved = account.ReadAdmission(pool: PhysicalMemory.Device);

                Assert.True(condition: ((reserved.Held + reserved.Promised) <= reserved.Limit!.Value), userMessage: $"a granted growth passed the limit at budget {budget} GiB");
                Assert.True(condition: Allocate(allocation: ++next, bytes: replacement, family: RegionFamily, ledger: ledger));
                if (regionBuffer != 0) { Assert.True(condition: ledger.CountReleased(allocation: regionBuffer, device: Device)); }
                regionBuffer = next;
                capacity = replacement;
                Assert.True(condition: region.TryReserve(peak: capacity));
                grown++;
            } else {
                var refused = account.ReadAdmission(pool: PhysicalMemory.Device);

                // A refused reservation changes nothing.
                Assert.Equal(expected: (before.Held, before.Promised), actual: (refused.Held, refused.Promised));
                refusals++;
            }

            // Fixed storage is never refused: past the limit it is admitted and counted.
            Assert.True(condition: Allocate(allocation: ++next, bytes: (64L * Mebibyte), family: "view/targets", ledger: ledger));

            var after = account.ReadAdmission(pool: PhysicalMemory.Device);

            // What stands past the limit is the fixed storage admitted past it and what was held when the limit fell:
            // elastic storage never adds to it.
            Assert.Equal(expected: capacity, actual: after.ClassHeld[((int)MemoryClass.Elastic)]);
            Assert.Equal(expected: 0L, actual: after.Promised);
            if ((after.Held + after.Promised) > after.Limit!.Value) {
                Assert.True(condition: ((after.OverLimitGrants > 0) || (after.Limit.Value < before.Held)), userMessage: $"the pool passed its limit uncounted at budget {budget} GiB");
            }
        }

        var final = account.ReadAdmission(pool: PhysicalMemory.Device);

        Assert.True(condition: (grown > 0));
        Assert.True(condition: (refusals > 0));
        Assert.Equal(expected: ((long)refusals), actual: final.Refusals);
        Assert.Equal(expected: final.Held, actual: final.ClassHeld.Sum());
        // The region stopped growing where the falling budget met it: it holds no more than the limit that admitted its
        // last growth, and the elastic bytes never passed a limit that admitted them.
        Assert.True(condition: (capacity <= (8L * Gibibyte)));
        Assert.True(condition: (final.OverLimit > 0L));
    }
    [Fact]
    public void AnAllocationAReservationCoversAddsNoPromiseAndOneItDoesNotIsRefusedForAnElasticOwner() {
        var (account, ledger, segments) = Discrete();

        Budget(account: account, localBudget: (2L * Gibibyte), segments: segments, serial: 1UL);

        var limit = account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value;
        using var region = account.ReserveDevice(memoryClass: MemoryClass.Elastic, owner: RegionOwner, pool: PhysicalMemory.Device);

        Assert.True(condition: region.TryReserve(peak: limit));
        Assert.Equal(expected: (0L, limit), actual: (account.ReadAdmission(pool: PhysicalMemory.Device).Held, account.ReadAdmission(pool: PhysicalMemory.Device).Promised));
        // The whole pool is promised to the region, and its own allocation still fits: the promise is consumed, never
        // counted twice.
        Assert.True(condition: Allocate(allocation: 0x10, bytes: limit, family: RegionFamily, ledger: ledger));
        Assert.Equal(expected: (limit, 0L), actual: (account.ReadAdmission(pool: PhysicalMemory.Device).Held, account.ReadAdmission(pool: PhysicalMemory.Device).Promised));
        // Past its reservation an elastic owner is refused, and nothing is counted.
        Assert.False(condition: Allocate(allocation: 0x11, bytes: Mebibyte, family: RegionFamily, ledger: ledger));
        Assert.False(condition: region.TryReserve(peak: (limit + Mebibyte)));
        Assert.Equal(expected: limit, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Held);
        // A reservation lowered under what its owner holds takes nothing back.
        Assert.True(condition: region.TryReserve(peak: 0L));
        Assert.Equal(expected: (limit, 0L), actual: (account.ReadAdmission(pool: PhysicalMemory.Device).Held, account.ReadAdmission(pool: PhysicalMemory.Device).Promised));
        Assert.True(condition: ledger.CountReleased(allocation: 0x10, device: Device));
        Assert.Equal(expected: 0L, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Held);
    }
    [Fact]
    public void AFloorIsReservedWhateverThePoolHoldsAndReadsOverTheLimit() {
        var (account, ledger, segments) = Discrete();

        Budget(account: account, localBudget: Gibibyte, segments: segments, serial: 1UL);

        var limit = account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value;
        using var region = account.ReserveDevice(memoryClass: MemoryClass.Elastic, owner: RegionOwner, pool: PhysicalMemory.Device);

        Assert.True(condition: Allocate(allocation: 0x10, bytes: limit, family: "view/targets", ledger: ledger));
        // Nothing is left, a peak past the floor is refused, and the floor itself is reserved and allocated.
        Assert.False(condition: region.TryReserve(floor: Mebibyte, peak: (2L * Mebibyte)));
        Assert.True(condition: region.TryReserve(floor: Mebibyte, peak: Mebibyte));
        Assert.True(condition: Allocate(allocation: 0x11, bytes: Mebibyte, family: RegionFamily, ledger: ledger));

        var admission = account.ReadAdmission(pool: PhysicalMemory.Device);

        Assert.Equal(expected: (Mebibyte, 0L), actual: (admission.OverLimit, admission.Promised));
    }
    [Fact]
    public void ADeviceRefusalLowersTheSegmentsLimitToWhatItHoldsUntilItsBudgetChanges() {
        var (account, ledger, segments) = Discrete();

        Budget(account: account, localBudget: (8L * Gibibyte), segments: segments, serial: 1UL);
        Assert.True(condition: Allocate(allocation: 0x10, bytes: Gibibyte, family: "view/targets", ledger: ledger));
        Assert.True(condition: ledger.TryAdmit(bytes: Gibibyte, device: Device, family: "view/targets", grant: out var grant, payer: "view/targets", payerBytes: Gibibyte, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        Assert.Equal(expected: Gibibyte, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Promised);

        var refusal = grant.Refuse();

        Assert.True(condition: refusal.ByDevice);
        // The grant is settled once.
        _ = Assert.Throws<InvalidOperationException>(testCode: grant.Return);

        var admission = account.ReadAdmission(pool: PhysicalMemory.Device);

        Assert.Equal(expected: (Gibibyte, 0L, Gibibyte, 1L, 1L), actual: (admission.Held, admission.Promised, admission.Limit!.Value, admission.DeviceRefusals, ledger.DeviceRefusals));

        using var region = account.ReserveDevice(memoryClass: MemoryClass.Elastic, owner: RegionOwner, pool: PhysicalMemory.Device);

        Assert.False(condition: region.TryReserve(peak: Mebibyte));
        // The same budget read again leaves the lowered limit; a budget that changed lifts it.
        Budget(account: account, localBudget: (8L * Gibibyte), segments: segments, serial: 1UL);
        Assert.Equal(expected: Gibibyte, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value);
        Budget(account: account, localBudget: (7L * Gibibyte), segments: segments, serial: 2UL);
        Assert.True(condition: (account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value > (6L * Gibibyte)));
        Assert.True(condition: region.TryReserve(peak: Mebibyte));
    }
    [Fact]
    public void OneForcedJobIsAdmittedAtATimeAcrossEveryOwner() {
        var account = new MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: (64L * Gibibyte));

        account.SetBudget(bytes: account.Headroom);

        using var first = account.CreateOwner(memoryClass: MemoryClass.Workspace, name: "bake-a");
        using var second = account.CreateOwner(memoryClass: MemoryClass.Workspace, name: "bake-b");
        using var third = account.CreateOwner(memoryClass: MemoryClass.Workspace, name: "bake-c");

        Assert.False(condition: first.TryReserve(peak: Gibibyte));
        Assert.True(condition: first.TryReserveForced(peak: Gibibyte));
        Assert.False(condition: second.TryReserveForced(peak: Gibibyte));
        Assert.False(condition: third.TryReserveForced(peak: Mebibyte));

        var forced = account.ReadAdmission(pool: PhysicalMemory.Host);

        // The one forced job is what stands past the limit, and it is counted.
        Assert.Equal(expected: (Gibibyte, 1L, 0L, Gibibyte), actual: (forced.Forced, forced.ForcedJobs, forced.OverLimit, forced.Promised));
        Assert.Single(collection: account.ReadOwners().Owners, predicate: static owner => owner.Forced);
        first.ReleaseReservation();
        Assert.Equal(expected: 0L, actual: account.ReadAdmission(pool: PhysicalMemory.Host).Forced);
        Assert.True(condition: second.TryReserveForced(peak: Gibibyte));
        Assert.False(condition: first.TryReserveForced(peak: Gibibyte));
        second.Dispose();
        Assert.True(condition: third.TryReserveForced(peak: Mebibyte));
        Assert.Equal(expected: 3L, actual: account.ReadAdmission(pool: PhysicalMemory.Host).ForcedJobs);
    }
    [Fact]
    public void TheHostPoolIsHeldToTheFreeMemoryTheAccountReads() {
        var free = ((long?)(8L * Gibibyte));
        var account = new MemoryAccount(availablePhysicalBytes: () => free, installedPhysicalBytes: (64L * Gibibyte));
        using var owner = account.CreateOwner(memoryClass: MemoryClass.Workspace, name: "job");

        Assert.Equal(expected: ((8L * Gibibyte) - account.Headroom), actual: account.ReadAdmission(pool: PhysicalMemory.Host).Limit!.Value);
        Assert.False(condition: owner.TryReserve(peak: (8L * Gibibyte)));
        Assert.True(condition: owner.TryReserve(peak: ((8L * Gibibyte) - account.Headroom)));
        Assert.Equal(expected: 1L, actual: account.ReadAdmission(pool: PhysicalMemory.Host).Refusals);
    }
    [Fact]
    public void ALimitThatFallsUnderWhatAPoolHoldsFiresOneTrim() {
        var (account, ledger, segments) = Discrete();
        var trims = 0;

        account.TrimRequested += () => trims++;
        Budget(account: account, localBudget: (8L * Gibibyte), segments: segments, serial: 1UL);
        Assert.True(condition: Allocate(allocation: 0x10, bytes: (4L * Gibibyte), family: "view/targets", ledger: ledger));
        Assert.False(condition: account.ServicePressure());
        Budget(account: account, localBudget: (2L * Gibibyte), segments: segments, serial: 2UL);
        Assert.True(condition: account.ServicePressure());
        // Pressure fires once for the reading that showed it, and no sooner than its interval again.
        Assert.False(condition: account.ServicePressure());
        Budget(account: account, localBudget: Gibibyte, segments: segments, serial: 3UL);
        Assert.False(condition: account.ServicePressure());
        Assert.Equal(expected: (1, 1L), actual: (trims, account.ReadAdmission(pool: PhysicalMemory.Device).PressureTrims));
    }
    [Fact]
    public void ADeviceBudgetSetByHandStandsInForTheReadingAndAutoRestoresIt() {
        var (account, _, segments) = Discrete();

        Budget(account: account, localBudget: (8L * Gibibyte), segments: segments, serial: 1UL);

        var read = account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value;

        account.SetBudget(bytes: Gibibyte, pool: PhysicalMemory.Device);
        Assert.Equal(expected: Gibibyte, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value);
        account.SetBudget(bytes: null, pool: PhysicalMemory.Device);
        Assert.Equal(expected: read, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value);
    }
}
