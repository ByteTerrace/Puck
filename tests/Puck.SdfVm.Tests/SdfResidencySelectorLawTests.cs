using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for a presentation's selector (<see cref="SdfResidencySelector"/>) and its background work budget
/// (<see cref="SdfGpuWorkBudget"/>): the residencies' grants add up under the pool's limit however many there are and
/// however far the limit falls; one bias coarsens every residency, rising when the budget presses with nothing left to
/// give up and falling when every residency fits again; a region grows only under its grant's reservation; and the
/// background budget is one rate of wall time for every service.
/// </summary>
public sealed class SdfResidencySelectorLawTests {
    private const nint Device = 0xD0;
    private const long Gibibyte = (1L << 30);
    private const long Mebibyte = (1L << 20);

    private static (MemoryAccount Account, GpuDeviceMemoryWork Ledger, GpuMemorySegments Segments) Discrete() {
        var account = new MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: (64L * Gibibyte));
        var ledger = new GpuDeviceMemoryWork(backend: "law", owners: account.DeviceOwners(backend: "law"));
        var segments = GpuMemorySegments.FromDirectX(profile: default);

        ledger.BeginDevice(device: Device, segments: segments);

        return (account, ledger, segments);
    }
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

    [Fact]
    public void FourResidenciesGrantsSumUnderTheLimitAtEveryStepOfAFallingBudget() {
        var (account, ledger, segments) = Discrete();
        var selector = new SdfResidencySelector(account: account, pool: PhysicalMemory.Device);
        var grants = new[] { selector.Register(name: "valley"), selector.Register(name: "gallery"), selector.Register(name: "painting"), selector.Register(name: "moon") };
        // What each residency's draws name: an occupied valley, and three smaller worlds behind glass.
        var named = new[] { (1200L * Mebibyte), (300L * Mebibyte), (40L * Mebibyte), 0L };
        var serial = 0UL;

        // The views' fixed storage, which the regions' budget is what the limit leaves beside.
        Assert.True(condition: ledger.TryAdmit(bytes: (700L * Mebibyte), device: Device, family: "view/targets", grant: out var targets, payer: "view/targets", payerBytes: (700L * Mebibyte), role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        ledger.CountAllocated(allocation: 0x10, grant: targets);
        for (var index = 0; (index < grants.Length); index++) { grants[index].Created(capacityBytes: Mebibyte); }

        foreach (var budget in new[] { 12L, 8L, 6L, 4L, 3L, 2L, 1L }) {
            Budget(account: account, localBudget: (budget * Gibibyte), segments: segments, serial: ++serial);

            var budgets = new long[grants.Length];

            for (var index = 0; (index < grants.Length); index++) { budgets[index] = grants[index].Budget(filled: named[index], named: named[index]); }
            // Every residency is asked again so that each answer saw every other's bytes.
            for (var index = 0; (index < grants.Length); index++) { budgets[index] = grants[index].Budget(filled: named[index], named: named[index]); }

            var (granted, limit) = selector.ReadGrants();
            var admission = account.ReadAdmission(pool: PhysicalMemory.Device);

            Assert.Equal(expected: admission.Limit, actual: limit);
            Assert.True(condition: (granted.Sum() <= limit!.Value), userMessage: $"the grants pass the limit at {budget} GiB");
            // The grants also leave the fixed storage its bytes.
            Assert.True(condition: ((granted.Sum() + (700L * Mebibyte)) <= Math.Max(val1: limit.Value, val2: (700L * Mebibyte))), userMessage: $"the grants take the views' bytes at {budget} GiB");
            // A residency over the budget is over it with every other: each is granted less than it names, or none is.
            var over = budgets.Zip(resultSelector: static (budget, bytes) => ((bytes > 0L) && (budget < bytes)), second: named).ToArray();

            Assert.True(condition: (over.Count(predicate: static pressed => pressed) is 0 or 3), userMessage: $"the residencies press unevenly at {budget} GiB");
        }

        Assert.Contains(expectedSubstring: "residencies=4", actualString: selector.Describe());
        Assert.Contains(expectedSubstring: "grant valley=", actualString: selector.Describe());
        foreach (var grant in grants) { grant.Dispose(); }
        Assert.Equal(expected: 0L, actual: account.ReadAdmission(pool: PhysicalMemory.Device).Promised);
    }
    [Fact]
    public void AnUnboundedPoolGrantsEveryResidencyWithoutABudget() {
        var (account, _, _) = Discrete();
        var selector = new SdfResidencySelector(account: account, pool: PhysicalMemory.Device);
        using var grant = selector.Register(name: "valley");

        Assert.Equal(expected: long.MaxValue, actual: grant.Budget(filled: Gibibyte, named: Gibibyte));
        Assert.Equal(expected: (64L * Gibibyte), actual: grant.ReserveGrowth(capacityBytes: Mebibyte, mostBytes: (64L * Gibibyte)));
    }
    [Fact]
    public void TheOneBiasRisesWhenTheBudgetPressesWithNothingSpareAndFallsOnceEveryResidencyFits() {
        var (account, _, segments) = Discrete();
        var selector = new SdfResidencySelector(account: account, pool: PhysicalMemory.Device);
        using var valley = selector.Register(name: "valley");
        using var painting = selector.Register(name: "painting");

        Budget(account: account, localBudget: (2L * Gibibyte), segments: segments, serial: 1UL);
        _ = valley.Budget(filled: Gibibyte, named: Gibibyte);
        _ = painting.Budget(filled: (10L * Mebibyte), named: (10L * Mebibyte));

        var pressing = new SdfDetailReport(AnySpare: false, CanLower: false, Elapsed: SdfMeshTier.MeasureFrames, Over: true);
        var spare = (pressing with { AnySpare = true });
        var quiet = new SdfDetailReport(AnySpare: false, CanLower: true, Elapsed: SdfMeshTier.MeasureFrames, Over: false);
        // The decisions of the largest residency that span the hold.
        var decisions = (((SdfMeshTier.HoldFrames + SdfMeshTier.MeasureFrames) - 1) / SdfMeshTier.MeasureFrames);

        // While any residency still holds detail it can give up, the bias waits.
        _ = painting.Decide(report: in spare);
        for (var index = 0; (index < (4 * decisions)); index++) { _ = valley.Decide(report: in pressing); }
        Assert.Equal(expected: 0, actual: selector.Bias);
        // With nothing spare anywhere it rises, by the report of the residency that names the most, and every residency
        // reads the same bias. The smaller residency's reports do not advance it.
        _ = painting.Decide(report: in pressing);
        for (var index = 0; (index < decisions); index++) { _ = painting.Decide(report: in pressing); }
        Assert.Equal(expected: 0, actual: selector.Bias);
        for (var index = 0; (index < decisions); index++) { _ = valley.Decide(report: in pressing); }
        Assert.Equal(expected: (1, 1, 1), actual: (selector.Bias, valley.Bias, painting.Bias));
        // It falls only once every residency fits half a rung finer.
        _ = painting.Decide(report: new SdfDetailReport(AnySpare: false, CanLower: false, Elapsed: SdfMeshTier.MeasureFrames, Over: false));
        for (var index = 0; (index < ((SdfMeshTier.BiasQuietFrames / SdfMeshTier.MeasureFrames) + 1)); index++) { _ = valley.Decide(report: in quiet); }
        Assert.Equal(expected: 1, actual: selector.Bias);
        _ = painting.Decide(report: in quiet);
        for (var index = 0; (index < ((SdfMeshTier.BiasQuietFrames / SdfMeshTier.MeasureFrames) + 1)); index++) { _ = valley.Decide(report: in quiet); }
        Assert.Equal(expected: (0, (1, 1)), actual: (selector.Bias, selector.BiasSteps));
    }
    [Fact]
    public void ARegionGrowsOnlyUnderItsGrantsReservationAndARefusedGrowthKeepsWhatStands() {
        var (account, ledger, segments) = Discrete();
        var selector = new SdfResidencySelector(account: account, pool: PhysicalMemory.Device);
        using var grant = selector.Register(name: "valley");

        Budget(account: account, localBudget: (2L * Gibibyte), segments: segments, serial: 1UL);

        var limit = account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value;

        grant.Created(capacityBytes: (16L * Mebibyte));
        Assert.True(condition: ledger.TryAdmit(bytes: (16L * Mebibyte), device: Device, family: "sdf.mesh-region/mesh-region", grant: out var first, payer: "sdf.mesh-region/mesh-region", payerBytes: (16L * Mebibyte), role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        ledger.CountAllocated(allocation: 0x10, grant: first);

        // The region may grow into the room the pool has, and the reservation that says so covers the standing buffer
        // and a replacement of that size together.
        var most = grant.ReserveGrowth(capacityBytes: (16L * Mebibyte), mostBytes: (64L * Gibibyte));
        var reserved = account.ReadAdmission(pool: PhysicalMemory.Device);

        Assert.True(condition: (most > (16L * Mebibyte)));
        Assert.True(condition: ((reserved.Held + reserved.Promised) <= limit));
        Assert.True(condition: (reserved.Promised >= most));
        // The replacement is admitted against the reservation with nothing promised twice.
        Assert.True(condition: ledger.TryAdmit(bytes: (512L * Mebibyte), device: Device, family: "sdf.mesh-region/mesh-region", grant: out var second, payer: "sdf.mesh-region/mesh-region", payerBytes: (512L * Mebibyte), role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        ledger.CountAllocated(allocation: 0x11, grant: second);
        grant.Grown(newBytes: (512L * Mebibyte), oldBytes: (16L * Mebibyte));
        Assert.Equal(expected: (16L * Mebibyte), actual: account.ReadAdmission(pool: PhysicalMemory.Device).Retired);
        Assert.True(condition: ledger.CountReleased(allocation: 0x10, device: Device));
        grant.Released(bytes: (16L * Mebibyte));

        var grown = account.ReadAdmission(pool: PhysicalMemory.Device);

        Assert.Equal(expected: ((512L * Mebibyte), 0L, 0L), actual: (grown.Held, grown.Promised, grown.Retired));
        // With the pool filled by something else, the region keeps its buffer: the limit it is told is what it holds.
        Assert.True(condition: ledger.TryAdmit(bytes: ((limit - grown.Held) - Mebibyte), device: Device, family: "view/targets", grant: out var targets, payer: "view/targets", payerBytes: 0L, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal));
        ledger.CountAllocated(allocation: 0x12, grant: targets);
        Assert.Equal(expected: (512L * Mebibyte), actual: grant.ReserveGrowth(capacityBytes: (512L * Mebibyte), mostBytes: (64L * Gibibyte)));
        Assert.True(condition: ((account.ReadAdmission(pool: PhysicalMemory.Device).Held + account.ReadAdmission(pool: PhysicalMemory.Device).Promised) <= limit));
        Assert.Contains(expectedSubstring: "growth-refusals=0", actualString: selector.Describe());
    }
    [Fact]
    public void TheWorkBudgetIsOneRateOfWallTimeForEveryService() {
        var now = Stopwatch.Frequency;
        var budget = new SdfGpuWorkBudget(clock: () => now);
        var charged = 0.0;

        // Two services at ten frames a second, each taking a frame's share of jobs while the budget holds credit: over
        // five seconds they are charged the budget's share of that time between them, not a share each.
        budget.Accrue();
        for (var frame = 0; (frame < 50); frame++) {
            now += (Stopwatch.Frequency / 10);
            for (var service = 0; (service < 2); service++) {
                budget.Accrue();
                while (budget.Holds) {
                    budget.ChargeGpu(seconds: SdfGpuWork.FrameShareSeconds);
                    charged += SdfGpuWork.FrameShareSeconds;
                }
                budget.Want();
            }
        }

        var share = (charged / 5.0);

        Assert.InRange(actual: share, high: (SdfGpuWork.GpuShare * 1.1), low: (SdfGpuWork.GpuShare * 0.9));

        // With nobody waiting on it, a gap banks one frame's share and no more.
        var idle = new SdfGpuWorkBudget(clock: () => now);

        idle.Accrue();
        idle.ChargeGpu(seconds: SdfGpuWork.FrameShareSeconds);
        now += (10L * Stopwatch.Frequency);
        idle.Accrue();

        var jobs = 0;

        while (idle.Holds) {
            idle.ChargeGpu(seconds: SdfGpuWork.FrameShareSeconds);
            jobs++;
        }
        Assert.Equal(actual: jobs, expected: 1);
    }
    [Fact]
    public void AReportMadeBeforeTheLimitWasRestoredDoesNotRaiseTheBias() {
        var (account, _, segments) = Discrete();
        var selector = new SdfResidencySelector(account: account, pool: PhysicalMemory.Device);
        using var valley = selector.Register(name: "valley");
        using var painting = selector.Register(name: "painting");
        var pressing = new SdfDetailReport(AnySpare: false, CanLower: false, Elapsed: SdfMeshTier.MeasureFrames, Over: true);
        var fitting = new SdfDetailReport(AnySpare: false, CanLower: false, Elapsed: SdfMeshTier.MeasureFrames, Over: false);

        // Under a lowered budget both residencies name more than it holds, and the bias rises on the larger one's
        // report.
        account.SetBudget(bytes: (512L * Mebibyte), pool: PhysicalMemory.Device);
        Budget(account: account, localBudget: (8L * Gibibyte), segments: segments, serial: 1UL);
        _ = valley.Budget(filled: Gibibyte, named: Gibibyte);
        _ = painting.Budget(filled: (10L * Mebibyte), named: (10L * Mebibyte));
        _ = valley.Decide(report: in pressing);
        _ = painting.Decide(report: in pressing);
        Assert.Equal(expected: 1, actual: selector.Bias);
        // The limit is restored. The painting's last report still says the budget presses, and is a moment old; the
        // valley decides against the limit as it stands, where everything named fits, and the bias does not rise.
        account.SetBudget(bytes: null, pool: PhysicalMemory.Device);
        for (var decision = 0; (decision < 8); decision++) {
            _ = valley.Budget(filled: Gibibyte, named: Gibibyte);
            _ = valley.Decide(report: in fitting);
        }
        Assert.Equal(expected: (1, (1, 0)), actual: (selector.Bias, selector.BiasSteps));
        // And what every residency names passing the budget presses whatever the deciding one reports of itself.
        account.SetBudget(bytes: (512L * Mebibyte), pool: PhysicalMemory.Device);
        _ = valley.Decide(report: in fitting);
        Assert.Equal(expected: 2, actual: selector.Bias);
    }
    [Fact]
    public void AMeshRegionsStagingIsAdmittedOnUnifiedMemoryWhereItsGrowthIsRefused() {
        // Unified memory: one segment in the host's pool, which the regions' buffers and their staging share.
        var account = new MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: (64L * Gibibyte));
        var ledger = new GpuDeviceMemoryWork(backend: "law", owners: account.DeviceOwners(backend: "law"));
        var segments = GpuMemorySegments.FromDirectX(profile: new GpuMemoryProfile(CoherentUnifiedMemory: true, DeviceLocalBytes: (16UL << 30), HostVisibleDeviceLocalBytes: (16UL << 30), LargestDeviceLocalHeapBytes: (16UL << 30), UnifiedMemory: true));
        var selector = new SdfResidencySelector(account: account, pool: PhysicalMemory.Host);
        using var grant = selector.Register(name: "valley");
        const string RegionFamily = (SdfResidencySelector.RegionOwner + "/mesh-region");
        const string StagingFamily = (SdfResidencySelector.RegionStagingOwner + "/mesh-region");

        bool Allocate(nint allocation, long bytes, string family, out bool overLimit) {
            overLimit = false;
            if (!ledger.TryAdmit(bytes: bytes, device: Device, family: family, grant: out var admitted, payer: family, payerBytes: bytes, role: GpuMemoryRole.DeviceLocal, segment: GpuMemorySegments.DirectXLocal)) { return false; }
            overLimit = admitted.OverLimit;
            ledger.CountAllocated(allocation: allocation, grant: admitted);

            return true;
        }

        ledger.BeginDevice(device: Device, segments: segments);
        account.SetBudget(bytes: (account.Headroom + (256L * Mebibyte)));
        grant.Created(capacityBytes: (64L * Mebibyte));
        Assert.True(condition: Allocate(allocation: 0x10, bytes: (64L * Mebibyte), family: RegionFamily, overLimit: out _));
        // The pool is filled to its limit by the views.
        Assert.True(condition: Allocate(allocation: 0x11, bytes: (192L * Mebibyte), family: "view/targets", overLimit: out var first));
        Assert.False(condition: first);
        Assert.Equal(expected: 0L, actual: account.ReadAdmission(pool: PhysicalMemory.Host).Room);
        // The region cannot grow: its limit is what it holds, and an allocation past it is refused.
        Assert.Equal(expected: (64L * Mebibyte), actual: grant.ReserveGrowth(capacityBytes: (64L * Mebibyte), mostBytes: (64L * Gibibyte)));
        Assert.False(condition: Allocate(allocation: 0x12, bytes: (128L * Mebibyte), family: RegionFamily, overLimit: out _));
        // Its staging is transfer storage under another owner: a flush that stages more than any before it is admitted,
        // past the limit and counted, never refused, so an upload does not throw for want of room.
        Assert.True(condition: Allocate(allocation: 0x13, bytes: (128L * Mebibyte), family: StagingFamily, overLimit: out var staged));
        Assert.True(condition: staged);
        Assert.Equal(expected: ((128L * Mebibyte), 1L), actual: (account.ReadAdmission(pool: PhysicalMemory.Host).OverLimit, account.ReadAdmission(pool: PhysicalMemory.Host).OverLimitGrants));
    }
}
