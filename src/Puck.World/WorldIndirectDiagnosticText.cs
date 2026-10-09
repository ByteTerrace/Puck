using System.Globalization;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
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
        var inventory = ((residencies.Count == 0) ? "indirect off, 0 byte(s)" :
            string.Join(separator: " | ", values: residencies.OrderBy(residency => residency.Name, StringComparer.Ordinal)
                .Select(selector: residency => Describe(probe: probe, residency: residency))));

        if ((captured is null) || (capturedResidency is null)) { return inventory; }
        return (((inventory + " | ") + string.Create(CultureInfo.InvariantCulture,
            $"retained-pixel residency={capturedResidency.Name} allocation={(captured.Cache?.Allocation ?? 0)} epoch={(captured.Cache?.Epoch ?? 0)} generation={captured.Generation} stamp={captured.Publication} source={(captured.LightingSource?.Sequence ?? 0)} sources=0x{((uint)captured.SourcesEnabled):x2} near={captured.Near.ToString().ToLowerInvariant()} "))
            + WorldIndirectPickText.DescribeCensus(pick: captured));
    }

    private static string Describe(SdfWorldResidency residency, WorldRenderProbe probe) {
        var light = probe.Root?.Runtime.NodeOf(instance: WorldViewNames.IndirectLight(cache: residency.IndirectInstanceName));
        var bytes = (residency.Tables?.IndirectBytes ?? default);
        var cache = residency.Tables?.Indirect;
        var memory = DescribeMemory(cache?.Layout, bytes, (cache?.Bytes ?? default), (cache?.LightViewBytes ?? 0UL), (light?.OwnedBytes ?? 0UL));
        var control = $"frozen={residency.IndirectFrozen} reset-pending={residency.IndirectResetPending}";

        if (cache is null) {
            var refusal = ((residency.IndirectRefusal is { } reason) ? $" refused=\"{reason}\"" : string.Empty);

            return $"indirect {residency.Name} tier={residency.IndirectTier.ToString().ToLowerInvariant()} {control} cache=unallocated{refusal} {memory}";
        }
        var snapshot = cache.Snapshot();
        var levels = snapshot.Levels.Select(selector: (level, index) => {
            var bricks = snapshot.Bricks.Where(predicate: brick => (brick.Key.Level == index)).ToArray();
            var placed = bricks.Count(predicate: brick => brick.Placed);
            var strata = bricks.Sum(selector: brick => brick.SubmittedStrata.Sum(selector: mask => BitOperations.PopCount(value: mask)));

            return string.Create(CultureInfo.InvariantCulture,
                $"{level.Name}(spacing={level.Spacing:0.###},bricks={bricks.Length},placed={placed},submitted-strata={strata})");
        });
        var maps = residency.IndirectLightViews;
        var valid = 0;

        for (var index = 0; (index < maps.MapCount); index++) { if (maps.Snapshot(index: index).Valid) { valid++; } }
        var mapRows = Enumerable.Range(0, maps.MapCount).Select(selector: index => {
            var map = maps.Snapshot(index: index);
            var projection = ((map.Projection is { } view)
                ? string.Create(CultureInfo.InvariantCulture, $"orthographic,texel-world={((2d * view.HalfWidth) / view.Resolution):0.######},sweep-radius={view.SweepRadius:0.######}")
                : "unprojectable");

            return $"map[{index}](valid={map.Valid},light-index={map.LightIndex},light-generation={map.LightGeneration},geometry-generation={map.GeometryGeneration},{projection})";
        });

        return string.Create(CultureInfo.InvariantCulture,
            $"indirect {residency.Name} tier={snapshot.Tier.ToString().ToLowerInvariant()} allocation={snapshot.Allocation} epoch={snapshot.Epoch} submission={snapshot.Submission} lighting-publication={cache.LightingPublication} {control} trace-complete={snapshot.TraceComplete} pending-place={snapshot.PendingPlacements} pending-classify={snapshot.PendingClassifications} pending-trace={snapshot.PendingTraces} pending-transport-chunks={cache.PendingTransportChunks} pending-shade={snapshot.PendingShades} invalidations={cache.Invalidations} remaining-frames={(cache.RemainingFrames?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "unmeasured")} prices={Prices(cache: cache)} sweeps={snapshot.CompletedSweeps} lighting-complete={snapshot.LightingComplete} published-generation={snapshot.PublishedGeneration} published-stamp={snapshot.PublishedStamp} published-source={(cache.PublishedLightingSource?.Sequence ?? 0)} levels={string.Join(separator: ',', values: levels)} light-maps={valid}/{maps.MapCount} light-pending={maps.Pending} light-publications={maps.Publications} maps={string.Join(separator: ',', values: mapRows)} gpu-probe-classes=unread gpu-irradiance=unread {memory}");
    }
    // Each measured kind's field visits per unit, or "-" before its first measurement: what admission prices it at.
    private static string Prices(SdfIndirectCache cache) {
        string[] names = ["place", "classify", "trace", "shade", "receiver"];

        return string.Join(separator: ',', values: names.Select(selector: (name, kind) => string.Create(CultureInfo.InvariantCulture,
            $"{name}:{(cache.MeasuredFieldCost(kind: kind)?.ToString(provider: CultureInfo.InvariantCulture) ?? "-")}")));
    }

    /// <summary>Formats disjoint slices of the active cache and separate actual active/retiring and light-fragment
    /// allocations. Probe-publication stamps, the shared receiver admission word and the split-probe shade scratch
    /// occupy separate cache slices.
    /// Per-view receiver completion words belong to graph storage outside these slices. The active cache includes
    /// its borrowed light banks; the light fragment counts only its own scratch and regions.</summary>
    /// <param name="layout">The active cache's word layout, or null when no active cache is allocated.</param>
    /// <param name="allCaches">The sum of unique active and retiring cache allocations.</param>
    /// <param name="activeCache">The active cache allocation, included in <paramref name="allCaches"/>.</param>
    /// <param name="lightDepth">The live depth-bank bytes already included in <paramref name="activeCache"/>.</param>
    /// <param name="lightFragment">The graph-owned light-fragment allocation, added once to the cache total.</param>
    /// <returns>Disjoint cache slices, active regions, retiring allocations and the complete byte total.</returns>
    /// <exception cref="OverflowException">The supplied allocation totals contradict their included slices, or the sum exceeds the byte counter.</exception>
    public static string DescribeMemory(SdfIndirectLayout? layout, GpuMemoryBytes allCaches, GpuMemoryBytes activeCache,
        ulong lightDepth, ulong lightFragment) {
        ulong Slice(int first, int end) => checked((((ulong)(end - first)) * sizeof(uint)));
        var state = ((layout is null) ? 0UL : Slice(layout.ProbeWordOffset, layout.CellWordOffset));
        var cells = ((layout is null) ? 0UL : Slice(layout.CellWordOffset, layout.HitWordOffset));
        var hits = ((layout is null) ? 0UL : Slice(layout.HitWordOffset, layout.ProofWordOffset));
        var proofs = ((layout is null) ? 0UL : Slice(layout.ProofWordOffset, layout.RadianceWordOffset));
        var radiance = ((layout is null) ? 0UL : Slice(layout.RadianceWordOffset, layout.IrradianceWordOffset));
        var irradiance = ((layout is null) ? 0UL : Slice(layout.IrradianceWordOffset, layout.PublicationWordOffset));
        var publication = ((layout is null) ? 0UL : Slice(layout.PublicationWordOffset, layout.ReceiverProofWordOffset));
        var receiverProofs = ((layout is null) ? 0UL : Slice(layout.ReceiverProofWordOffset, layout.ShadeScratchWordOffset));
        var shadeScratch = ((layout is null) ? 0UL : Slice(layout.ShadeScratchWordOffset, layout.CostWordOffset));
        var costCounters = ((layout is null) ? 0UL : Slice(layout.CostWordOffset, layout.WordCount));
        var regions = checked(((activeCache.DeviceLocal - (layout?.ByteLength ?? 0UL)) - lightDepth));
        var retiringDevice = checked((allCaches.DeviceLocal - activeCache.DeviceLocal));
        var retiringHost = checked((allCaches.HostVisible - activeCache.HostVisible));
        var total = checked(((allCaches.DeviceLocal + allCaches.HostVisible) + lightFragment));

        return string.Create(CultureInfo.InvariantCulture,
            $"hits={hits} cells={cells} state={state} proofs={proofs} irradiance={irradiance} radiance={radiance} publication={publication} receiver-proofs={receiverProofs} shade-scratch={shadeScratch} cost-counters={costCounters} regions-device={regions} regions-host={activeCache.HostVisible} retiring-device={retiringDevice} retiring-host={retiringHost} cache-device={allCaches.DeviceLocal} cache-host={allCaches.HostVisible} light-view={lightDepth} light-fragment={lightFragment} total={total} byte(s)");
    }
}
