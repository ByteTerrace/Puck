using System.Globalization;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Reads the host-owned indirect inventory and actual allocations on the console/frame owner thread.
/// A retained fenced answer is appended with its captured residency and publication; live host scheduling never
/// supplies or relabels GPU classifications.</summary>
public static class WorldIndirectDiagnosticText {
    /// <summary>Formats each unique active or retiring host cache and its actual allocation breakdown.</summary>
    /// <param name="probe">The live presentation inventory, or null when no renderer exists. Read on its console/frame owner thread.</param>
    /// <param name="captured">The last explicit explanation's fenced receiver, or null before one completes.</param>
    /// <param name="capturedResidency">The exact rendered residency that owns that retained answer.</param>
    /// <returns>The host inventory and separately qualified retained census, or a named unavailable/off result.</returns>
    public static string Describe(WorldRenderProbe? probe, SdfIndirectPick? captured = null, SdfWorldResidency? capturedResidency = null) {
        if (probe is null) { return "indirect unavailable: no renderer"; }
        var residencies = probe.IndirectAllocationResidencies;
        var inventory = residencies.Count == 0 ? "indirect off, 0 byte(s)" :
            string.Join(" | ", residencies.OrderBy(residency => residency.Name, StringComparer.Ordinal)
                .Select(residency => Describe(residency, probe)));
        if (captured is null || capturedResidency is null) { return inventory; }
        return inventory + " | " + string.Create(CultureInfo.InvariantCulture,
            $"retained-pixel residency={capturedResidency.Name} allocation={captured.Cache?.Allocation ?? 0} epoch={captured.Cache?.Epoch ?? 0} generation={captured.Generation} stamp={captured.Publication} source={captured.LightingSource?.Sequence ?? 0} sources=0x{(uint)captured.SourcesEnabled:x2} near={captured.Near.ToString().ToLowerInvariant()} ")
            + WorldIndirectPickText.DescribeCensus(captured);
    }

    private static string Describe(SdfWorldResidency residency, WorldRenderProbe probe) {
        var light = probe.Root?.Runtime.NodeOf(WorldViewNames.IndirectLight(residency.IndirectInstanceName));
        var depthName = RenderGraphPackageFragment.Spliced(SdfWorldPackage.IndirectLightDepth, RenderGraphPackageCatalog.SdfWorld);
        var depth = light?.ResourceStatus.Where(resource => string.Equals(resource.Name, depthName, StringComparison.Ordinal))
            .Aggregate(0UL, (total, resource) => checked(total + resource.AllocationBytes)) ?? 0UL;
        var bytes = residency.Tables?.IndirectBytes ?? default;
        var cache = residency.Tables?.Indirect;
        var memory = DescribeMemory(cache?.Layout, bytes, cache?.Bytes ?? default, depth, light?.OwnedBytes ?? 0UL);
        var control = $"frozen={residency.IndirectFrozen} reset-pending={residency.IndirectResetPending}";
        if (cache is null) { return $"indirect {residency.Name} tier={residency.IndirectTier.ToString().ToLowerInvariant()} {control} cache=unallocated {memory}"; }
        var snapshot = cache.Snapshot();
        var levels = snapshot.Levels.Select((level, index) => {
            var bricks = snapshot.Bricks.Where(brick => brick.Key.Level == index).ToArray();
            var placed = bricks.Count(brick => brick.Placed);
            var strata = bricks.Sum(brick => brick.SubmittedStrata.Sum(mask => BitOperations.PopCount(mask)));
            return string.Create(CultureInfo.InvariantCulture,
                $"{level.Name}(spacing={level.Spacing:0.###},bricks={bricks.Length},placed={placed},submitted-strata={strata})");
        });
        var maps = residency.IndirectLightViews;
        var valid = 0;
        for (var index = 0; index < maps.MapCount; index++) { if (maps.Snapshot(index).Valid) { valid++; } }
        var mapRows = Enumerable.Range(0, maps.MapCount).Select(index => {
            var map = maps.Snapshot(index);
            var projection = (map.Projection is { } view)
                ? string.Create(CultureInfo.InvariantCulture, $"orthographic,texel-world={2d * view.HalfWidth / view.Resolution:0.######},sweep-radius={view.SweepRadius:0.######}")
                : "unprojectable";
            return $"map[{index}](valid={map.Valid},light-index={map.LightIndex},light-generation={map.LightGeneration},geometry-generation={map.GeometryGeneration},{projection})";
        });
        return string.Create(CultureInfo.InvariantCulture,
            $"indirect {residency.Name} tier={snapshot.Tier.ToString().ToLowerInvariant()} allocation={snapshot.Allocation} epoch={snapshot.Epoch} submission={snapshot.Submission} lighting-publication={cache.LightingPublication} {control} trace-complete={snapshot.TraceComplete} pending-place={snapshot.PendingPlacements} pending-classify={snapshot.PendingClassifications} pending-trace={snapshot.PendingTraces} pending-shade={snapshot.PendingShades} sweeps={snapshot.CompletedSweeps} lighting-complete={snapshot.LightingComplete} published-generation={snapshot.PublishedGeneration} published-stamp={snapshot.PublishedStamp} published-source={cache.PublishedLightingSource?.Sequence ?? 0} levels={string.Join(',', levels)} light-maps={valid}/{maps.MapCount} light-pending={maps.Pending} light-publications={maps.Publications} maps={string.Join(',', mapRows)} gpu-probe-classes=unread gpu-irradiance=unread {memory}");
    }

    /// <summary>Formats disjoint slices of the active cache and separate actual active/retiring and light-fragment
    /// allocations. Probe-publication stamps and receiver admission/completion words occupy separate slices. A fragment's
    /// total already includes its depth bank and regions; these are breakdowns, not additions.</summary>
    /// <param name="layout">The active cache's word layout, or null when no active cache is allocated.</param>
    /// <param name="allCaches">The sum of unique active and retiring cache allocations.</param>
    /// <param name="activeCache">The active cache allocation, included in <paramref name="allCaches"/>.</param>
    /// <param name="lightDepth">The depth-bank bytes already included in <paramref name="lightFragment"/>.</param>
    /// <param name="lightFragment">The complete light-fragment allocation, added once to the cache total.</param>
    /// <returns>Disjoint cache slices, active regions, retiring allocations and the complete byte total.</returns>
    /// <exception cref="OverflowException">The supplied allocation totals contradict their included slices, or the sum exceeds the byte counter.</exception>
    public static string DescribeMemory(SdfIndirectLayout? layout, GpuMemoryBytes allCaches, GpuMemoryBytes activeCache,
        ulong lightDepth, ulong lightFragment) {
        ulong Slice(int first, int end) => checked((ulong)(end - first) * sizeof(uint));
        var state = (layout is null ? 0UL : Slice(layout.ProbeWordOffset, layout.CellWordOffset));
        var cells = (layout is null ? 0UL : Slice(layout.CellWordOffset, layout.HitWordOffset));
        var hits = (layout is null ? 0UL : Slice(layout.HitWordOffset, layout.ProofWordOffset));
        var proofs = (layout is null ? 0UL : Slice(layout.ProofWordOffset, layout.RadianceWordOffset));
        var radiance = (layout is null ? 0UL : Slice(layout.RadianceWordOffset, layout.IrradianceWordOffset));
        var irradiance = (layout is null ? 0UL : Slice(layout.IrradianceWordOffset, layout.PublicationWordOffset));
        var publication = (layout is null ? 0UL : Slice(layout.PublicationWordOffset, layout.ReceiverProofWordOffset));
        var receiverProofs = (layout is null ? 0UL : Slice(layout.ReceiverProofWordOffset, layout.WordCount));
        var regions = checked(activeCache.DeviceLocal - (layout?.ByteLength ?? 0UL));
        var retiringDevice = checked(allCaches.DeviceLocal - activeCache.DeviceLocal);
        var retiringHost = checked(allCaches.HostVisible - activeCache.HostVisible);
        var total = checked(allCaches.DeviceLocal + allCaches.HostVisible + lightFragment);
        return string.Create(CultureInfo.InvariantCulture,
            $"hits={hits} cells={cells} state={state} proofs={proofs} irradiance={irradiance} radiance={radiance} publication={publication} receiver-proofs={receiverProofs} regions-device={regions} regions-host={activeCache.HostVisible} retiring-device={retiringDevice} retiring-host={retiringHost} cache-device={allCaches.DeviceLocal} cache-host={allCaches.HostVisible} light-view={lightDepth} light-fragment={lightFragment} total={total} byte(s)");
    }
}
