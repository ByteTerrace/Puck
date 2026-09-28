using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a routed scene's GPU node reports its endpoint's row. Created less released reads the residencies the
/// presentation holds for the endpoint, which one residency per endpoint requires be one, and allocated less released reads
/// the bytes the residency's tables hold now in each memory, however the tables grow or shrink; every total only grows, as
/// every counter does. The residency's own lifetime kinds read through unchanged.
/// </summary>
public sealed class WorldRoutedResidencyCountsLawTests {
    private static long Read(WorldRoutedResidencyCounts counts, Abstractions.Counting.WorkKind kind) {
        Assert.True(condition: counts.TryRead(
            kind: kind,
            value: out var value
        ));

        return value;
    }
    private static (long DeviceLocal, long HostVisible) Held(WorldRoutedResidencyCounts counts) => (
        (Read(counts: counts, kind: WorldRoutedResidencyCounts.DeviceLocalAllocated) - Read(counts: counts, kind: WorldRoutedResidencyCounts.DeviceLocalReleased)),
        (Read(counts: counts, kind: WorldRoutedResidencyCounts.HostVisibleAllocated) - Read(counts: counts, kind: WorldRoutedResidencyCounts.HostVisibleReleased))
    );

    [Fact]
    public void AnEndpointsRowReadsItsResidenciesAndTheTableBytesHeldNow_EveryTotalOnlyGrowing() {
        var ledger = new GpuWorkLedger(
            framesInFlight: 1,
            name: "gpu.test"
        );
        var residencies = (Created: 1L, Released: 0L);
        var tables = new GpuMemoryBytes(DeviceLocal: 4096UL, HostVisible: 512UL);
        var counts = new WorldRoutedResidencyCounts(
            lifetime: ledger,
            residencies: () => residencies,
            tables: () => tables
        );

        Assert.Equal(
            actual: (Read(counts: counts, kind: WorldRoutedResidencyCounts.ResidenciesCreated) - Read(counts: counts, kind: WorldRoutedResidencyCounts.ResidenciesReleased)),
            expected: 1L
        );
        Assert.Equal(actual: Held(counts: counts), expected: (4096L, 512L));

        // The tables grow in device-local memory and give host-visible memory back.
        tables = new GpuMemoryBytes(DeviceLocal: 8192UL, HostVisible: 256UL);
        Assert.Equal(actual: Held(counts: counts), expected: (8192L, 256L));
        Assert.Equal(actual: Read(counts: counts, kind: WorldRoutedResidencyCounts.DeviceLocalAllocated), expected: 8192L);
        Assert.Equal(actual: Read(counts: counts, kind: WorldRoutedResidencyCounts.HostVisibleReleased), expected: 256L);

        // A second residency for the same endpoint reads as two.
        residencies = (2L, 0L);
        Assert.Equal(
            actual: (Read(counts: counts, kind: WorldRoutedResidencyCounts.ResidenciesCreated) - Read(counts: counts, kind: WorldRoutedResidencyCounts.ResidenciesReleased)),
            expected: 2L
        );

        // The residency's own lifetime kinds read through, and lead the kinds the node reports.
        Assert.Equal(actual: counts.WorkKinds[..GpuWork.LifetimeKinds.Length].ToArray(), expected: GpuWork.LifetimeKinds.ToArray());
        Assert.Equal(actual: counts.WorkKinds[GpuWork.LifetimeKinds.Length..].ToArray(), expected: [.. WorldRoutedResidencyCounts.Kinds]);
        Assert.Equal(actual: Read(counts: counts, kind: GpuWork.LifetimeKinds[0]), expected: 0L);
    }
}
