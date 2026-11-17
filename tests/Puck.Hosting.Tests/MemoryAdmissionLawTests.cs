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
    [Fact]
    public void NativePoolTrimSubscriptionFollowsTheAccountAndEndsAtTeardown() {
        var (account, ledger, _) = Discrete();
        var freed = new List<int>();
        var pool = new GpuMemoryPool<int, int>(1024, _ => 1, (block, _) => freed.Add(item: block));

        ledger.TrimRequested += pool.Trim;
        var live = pool.Place(0, 64, 64, static placement => placement);

        account.TrimCaches();
        Assert.Empty(collection: freed);
        pool.Return(placement: live);
        Assert.Equal(actual: freed, expected: [1]);
        ledger.TrimRequested -= pool.Trim;
        var next = pool.Place(0, 64, 64, static placement => placement);

        pool.Return(placement: next);
        account.TrimCaches();
        Assert.Equal(1, pool.Blocks);
        pool.FreeAll();
    }

    private const nint Device = 0xD0;
    private const long Gibibyte = (1L << 30);
    private const long Mebibyte = (1L << 20);
    private const string RegionFamily = "sdf.mesh-region/mesh-region";
    private const string RegionOwner = "sdf.mesh-region";

    // A discrete adapter's ledger reporting to an account that reads no free memory, so the host pool is its budget.
    private static (MemoryAccount Account, GpuDeviceMemoryWork Ledger, GpuMemorySegments Segments) Discrete(long installedBytes = (64L * Gibibyte), Func<long>? clock = null) {
        var account = new MemoryAccount(availablePhysicalBytes: static () => null, clock: clock, installedPhysicalBytes: installedBytes);
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
    public void AFloorAdmitsTheSharedBlockItsResourceIsPlacedInAndRefusesAResourcePastIt() {
        var (account, ledger, segments) = Discrete();

        Budget(account: account, localBudget: Gibibyte, segments: segments, serial: 1UL);

        var limit = account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value;
        using var work = account.ReserveDevice(memoryClass: MemoryClass.Workspace, owner: "work", pool: PhysicalMemory.Device);

        Assert.True(condition: Allocate(allocation: 0x10, bytes: limit, family: "view/targets", ledger: ledger));
        Assert.True(condition: work.TryReserve(floor: Mebibyte, peak: Mebibyte));
        // The device places a small resource in a block many times its size. The floor is the owner's own bytes, so the
        // block of a resource within it is admitted, past the limit and counted, as fixed storage is.
        Assert.True(condition: ledger.TryAdmit(bytes: (64L * Mebibyte), device: Device, family: null, grant: out var block, payer: "work/buffer", payerBytes: Mebibyte, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        ledger.CountAllocated(allocation: 0x11, grant: block);
        _ = ledger.CountPlaced(allocation: 0x11, bytes: Mebibyte, device: Device, family: "work/buffer");
        Assert.True(condition: (account.ReadAdmission(pool: PhysicalMemory.Device).OverLimit > 0L));
        // A second resource is past the floor: its block is refused, and nothing is counted.
        Assert.False(condition: ledger.TryAdmit(bytes: (64L * Mebibyte), device: Device, family: null, grant: out _, payer: "work/buffer", payerBytes: Mebibyte, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
    }
    [Fact]
    public void ADeviceRefusalLowersTheSegmentsLimitToWhatItHoldsUntilItsBudgetChanges() {
        // The clock stands still, so the refusal's probe never comes due and only the budget can lift it.
        var (account, ledger, segments) = Discrete(clock: static () => 1L);

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
    [Fact]
    public void TwoGrantsMadeBeforeEitherIsCountedCannotSpendOneReservationTwice() {
        var (account, ledger, segments) = Discrete();

        // The segment has room to spare; the pool holds exactly the reservation.
        Budget(account: account, localBudget: (8L * Gibibyte), segments: segments, serial: 1UL);
        account.SetBudget(bytes: (100L * Mebibyte), pool: PhysicalMemory.Device);

        using var region = account.ReserveDevice(memoryClass: MemoryClass.Elastic, owner: RegionOwner, pool: PhysicalMemory.Device);

        Assert.True(condition: region.TryReserve(peak: (100L * Mebibyte)));
        Assert.True(condition: ledger.TryAdmit(bytes: (100L * Mebibyte), device: Device, family: RegionFamily, grant: out var first, payer: RegionFamily, payerBytes: (100L * Mebibyte), role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        // The first grant drew the whole reservation under the account's lock: a second, made before the first is
        // counted, finds none of it left and no room, and is refused.
        Assert.False(condition: ledger.TryAdmit(bytes: (100L * Mebibyte), device: Device, family: RegionFamily, grant: out _, payer: RegionFamily, payerBytes: (100L * Mebibyte), role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        Assert.Equal(expected: (0L, (100L * Mebibyte)), actual: (account.ReadAdmission(pool: PhysicalMemory.Device).Held, account.ReadAdmission(pool: PhysicalMemory.Device).Promised));
        // A grant handed back gives the reservation its credit again, once.
        first.Return();
        Assert.Equal(expected: (100L * Mebibyte), actual: account.ReadAdmission(pool: PhysicalMemory.Device).Promised);
        Assert.True(condition: ledger.TryAdmit(bytes: (100L * Mebibyte), device: Device, family: RegionFamily, grant: out var again, payer: RegionFamily, payerBytes: (100L * Mebibyte), role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        ledger.CountAllocated(allocation: 0x10, grant: again);
        Assert.Equal(expected: ((100L * Mebibyte), 0L, 0L), actual: (account.ReadAdmission(pool: PhysicalMemory.Device).Held, account.ReadAdmission(pool: PhysicalMemory.Device).Promised, account.ReadAdmission(pool: PhysicalMemory.Device).OverLimit));
    }
    [Fact]
    public void TheFreeMemoryReadingIsSpentOnceHoweverLongItStands() {
        // One reading of a gibibyte free, which the clock never lets the account take again.
        var account = new MemoryAccount(availablePhysicalBytes: static () => Gibibyte, clock: static () => 1L, installedPhysicalBytes: (64L * Gibibyte));
        using var first = account.CreateOwner(memoryClass: MemoryClass.Workspace, name: "first");
        using var second = account.CreateOwner(memoryClass: MemoryClass.Workspace, name: "second");
        var allowance = (Gibibyte - account.Headroom);

        Assert.Equal(expected: allowance, actual: account.ReadAdmission(pool: PhysicalMemory.Host).Limit!.Value);
        Assert.True(condition: first.TryReserve(peak: allowance));
        first.Change(backing: MemoryBacking.NativeDirect, bytes: allowance);
        // What the first owner took came out of the reading: holding it does not make it free again.
        Assert.Equal(expected: allowance, actual: account.ReadAdmission(pool: PhysicalMemory.Host).Limit!.Value);
        Assert.False(condition: second.TryReserve(peak: Mebibyte));
        first.Change(backing: MemoryBacking.NativeDirect, bytes: -allowance);
        first.ReleaseReservation();
        Assert.True(condition: second.TryReserve(peak: allowance));
    }
    [Fact]
    public void ARefusedSegmentIsProbedAgainAfterItsWaitWhichDoublesWhileRefusalsFollow() {
        var now = 1L;

        var (account, ledger, segments) = Discrete(clock: () => now);
        using var region = account.ReserveDevice(memoryClass: MemoryClass.Elastic, owner: RegionOwner, pool: PhysicalMemory.Device);

        Budget(account: account, localBudget: (8L * Gibibyte), segments: segments, serial: 1UL);

        var read = account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value;

        void Refuse() {
            Assert.True(condition: ledger.TryAdmit(bytes: Mebibyte, device: Device, family: "view/targets", grant: out var grant, payer: null, payerBytes: 0L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
            _ = grant.Refuse();
        }

        // Refused while it holds nothing, the segment's limit is nothing: no elastic byte is admitted.
        Refuse();
        Assert.Equal(expected: 0L, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value);
        Assert.False(condition: region.TryReserve(peak: Mebibyte));
        // The same budget read again does not lift it, and the first wait does: the next allocation probes the device.
        Budget(account: account, localBudget: (8L * Gibibyte), segments: segments, serial: 1UL);
        now += (MemoryAccount.RefusalProbeInterval - 1L);
        Assert.Equal(expected: 0L, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value);
        now += 1L;
        Assert.Equal(expected: read, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value);
        // A probe the device refuses puts the limit back for twice as long.
        Refuse();
        now += MemoryAccount.RefusalProbeInterval;
        Assert.Equal(expected: 0L, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value);
        now += MemoryAccount.RefusalProbeInterval;
        Assert.Equal(expected: read, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value);
        // A probe the device makes ends the refusal: the storage grows again, and a later refusal waits the first
        // interval.
        Assert.True(condition: Allocate(allocation: 0x10, bytes: Mebibyte, family: "view/targets", ledger: ledger));
        Assert.True(condition: region.TryReserve(peak: Mebibyte));
        Refuse();
        now += MemoryAccount.RefusalProbeInterval;
        Assert.Equal(expected: read, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value);
        Assert.Equal(expected: 3L, actual: account.ReadAdmission(pool: PhysicalMemory.Device).DeviceRefusals);
    }
    [Fact]
    public void APoolStandingOverItsLimitAsksForOneTrimNotOneAtEveryReading() {
        var now = 1L;

        var (account, ledger, segments) = Discrete(clock: () => now);
        var trims = 0;

        account.TrimRequested += () => trims++;
        Budget(account: account, localBudget: (8L * Gibibyte), segments: segments, serial: 1UL);
        Assert.True(condition: Allocate(allocation: 0x10, bytes: (4L * Gibibyte), family: "view/targets", ledger: ledger));
        Budget(account: account, localBudget: (2L * Gibibyte), segments: segments, serial: 2UL);
        Assert.True(condition: account.ServicePressure());
        // The same budget read again and again, each a reading later, with the pool still over it: nothing new to trim.
        for (var reading = 0; (reading < 4); reading++) {
            now += (2L * MemoryAccount.PressureInterval);
            Budget(account: account, localBudget: (2L * Gibibyte), segments: segments, serial: 2UL);
            Assert.False(condition: account.ServicePressure());
        }
        // The excess growing is pressure again.
        Budget(account: account, localBudget: Gibibyte, segments: segments, serial: 3UL);
        Assert.True(condition: account.ServicePressure());
        Assert.Equal(actual: trims, expected: 2);
    }
    [Fact]
    public void AnOwnerThatAsksAgainWhileItWaitsIsOneRefusal() {
        var account = new MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: (64L * Gibibyte));

        account.SetBudget(bytes: account.Headroom);

        using var owner = account.CreateOwner(memoryClass: MemoryClass.Workspace, name: "waiting");

        for (var ask = 0; (ask < 100); ask++) { Assert.False(condition: owner.TryReserve(peak: Gibibyte)); }
        Assert.Equal(expected: 1L, actual: account.ReadAdmission(pool: PhysicalMemory.Host).Refusals);
        Assert.False(condition: owner.TryReserve(peak: (2L * Gibibyte)));
        Assert.Equal(expected: 2L, actual: account.ReadAdmission(pool: PhysicalMemory.Host).Refusals);
    }
    [Fact]
    public async Task TransientWorkWaitsForRoomAndDoesWithoutWhenItsPatienceRunsOut() {
        var account = new MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: (64L * Gibibyte));
        var reason = new Puck.Abstractions.Waiting.WaitReason(Subsystem: "law", WaitsFor: "room");

        account.SetBudget(bytes: account.Headroom);

        using var forced = account.CreateOwner(memoryClass: MemoryClass.Workspace, name: "forced");
        using var scratch = account.CreateOwner(memoryClass: MemoryClass.Workspace, name: "scratch");

        // With no room and the forced allowance held by another job, transient work is not admitted: it holds nothing,
        // and what the pool has promised is the forced job's alone.
        Assert.True(condition: forced.TryReserveForced(peak: Gibibyte));
        Assert.False(condition: scratch.TryReserveWithin(patience: TimeSpan.FromMilliseconds(milliseconds: 100), peak: Mebibyte, reason: in reason));
        Assert.Equal(expected: Gibibyte, actual: account.ReadAdmission(pool: PhysicalMemory.Host).Promised);

        // Once the forced job ends while it waits, it is admitted, as the one forced job.
        var release = Task.Run(action: () => {
            Thread.Sleep(millisecondsTimeout: 50);
            forced.ReleaseReservation();
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: scratch.TryReserveWithin(patience: TimeSpan.FromSeconds(seconds: 30), peak: Mebibyte, reason: in reason));
        await release;
        Assert.Equal(expected: (Mebibyte, 2L), actual: (account.ReadAdmission(pool: PhysicalMemory.Host).Forced, account.ReadAdmission(pool: PhysicalMemory.Host).ForcedJobs));
    }
}
