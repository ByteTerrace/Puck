using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The demand watch: which groups' demanded tiers a frame must measure again. A group's demanded tier is a function of
// its demand pixel (MeasurePixel) under the bias, and the pixel moves only when a demand point moves, the point set
// changes shape, or the group's box moves. So a frame measures:
//   - the groups set into a place since the last frame (GroupList.TakeFresh), which the watch has never measured;
//   - the groups whose box moved: a dynamic group whose slot's transform moved, and a group one of whose copies moved,
//     each found through the list's owner index (GroupList.FirstOf);
//   - the groups the demand points' travel made due: SdfMeshDemand.Travel bounds how far any pixel moves between two
//     observations of one revision, so a group measured at travel T with its pixel s (in travel units) from the nearest
//     edge of the tier band it stands in keeps its tier until the travel passes T + s (DemandDueAt, the due queue);
//   - and, after a change no travel bounds (the points' revision moved, the bias moved, the copy layout or a journal's
//     window was lost), every group once, spread over SdfMeshTier.MeasureFrames frames as one sweep.
// Invariant: a listed group's DemandPixel names the tier it stands at under the bias as of the frame's demand, except
// for a group a reset sweep has not reached yet, which is at most SdfMeshTier.MeasureFrames frames behind. Enforced by
// the four routes above being the only ones that can move a pixel: the travel bound is SdfMeshDemand's, the box routes
// read the frame's own journals, and every group enters the list through GroupList.Set. A change to what a pixel reads
// (a new input to MeasurePixel) must add its route here, or a group keeps a stale tier.
//
// The tier decisions read only the groups the watch measured since they last stood (the attention list, Attend): a
// group whose demanded tier is the one it draws, with no first detail owed and no replacement in flight, has nothing a
// decision can do until its pixel or its drawn tier moves, and both pass through a measure or a Set.
public sealed partial class SdfMeshPathBake {
    // The groups due by travel, each at the travel it falls due; an entry whose group was measured since (its due moved)
    // or left the list is passed over.
    private readonly PriorityQueue<Group, double> m_demandDue = new();
    // The revision the watch last read of the demand points, and the bias it measured under.
    private long m_demandRevision = -1L;
    private int m_demandBias = -1;
    // Whether a change no travel bounds asks every group to be measured again (IndexCopies); how many groups the sweep
    // doing so still owes, and where it stands; and the prepared frame the latest reset began at, since which a group
    // another route measured needs no measure from the sweep.
    private bool m_demandReset = true;
    private int m_demandSweepLeft;
    private GroupSweep m_demandSweep;
    private long m_demandResetAt;
    // The moved transforms the watch last read, and the slots they named.
    private readonly GpuRowCursor m_demandSlotCursor = new();
    private readonly GpuUploadRuns m_demandSlots = new(GpuRegion.MaxCopyRuns);
    // One more each time a measure moves a group's tier one half rung finer than the bias asks, which the bytes that
    // tier would name (Retier's finer figure) are cached against.
    private long m_finerDemandRevision;
    // The groups the tier decisions read (Attend).
    private readonly List<Group> m_attention = [];

    // Measures again the groups whose demand may have moved since the last frame, as the header says. Under the gate,
    // on the frame's thread.
    private void MeasureDemands(SdfFrame frame) {
        var travel = Demand.Travel;
        var bias = Volatile.Read(location: ref m_bias);
        var slotsWhole = m_demandSlotCursor.Collect(frame.MovedTransforms?.Changes, frame.MovedTransformsSerial, frame.DynamicTransforms,
            frame.DynamicTransforms.Count, 0, m_demandSlots);

        m_demandSlotCursor.Commit();
        if (m_demandReset || slotsWhole || m_copiesWhole || (Demand.Revision != m_demandRevision) || (bias != m_demandBias)) {
            m_demandReset = false;
            m_demandRevision = Demand.Revision;
            m_demandBias = bias;
            m_demandSweepLeft = m_groups.Count;
            m_demandResetAt = m_preparedFrames;
            m_demandDue.Clear();
            SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.DemandResets);
        }

        var stamp = m_preparedFrames;
        var groups = 0;
        var boxes = 0L;
        var due = 0;

        foreach (var group in m_groups.TakeFresh()) { Measure(group: group); }
        if (!slotsWhole) {
            for (var run = 0; (run < m_demandSlots.Count); run++) {
                var end = (m_demandSlots.Start(index: run) + m_demandSlots.Length(index: run));

                for (var slot = m_demandSlots.Start(index: run); (slot < end); slot++) { MeasureOwners(dynamic: true, key: slot); }
            }
        }
        if (!m_copiesWhole) {
            for (var run = 0; (run < m_copyRuns.Count); run++) {
                var end = Math.Min(val1: (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run)), val2: m_demandCopies.Count);

                for (var index = m_copyRuns.Start(index: run); (index < end); index++) {
                    var copy = m_demandCopies[index];

                    if (copy.TemplateRange is { } range) {
                        for (var instance = 0; (instance < range.InstanceCount); instance++) { MeasureOwners(dynamic: false, key: (range.FirstInstance + instance)); }
                        continue;
                    }
                    MeasureOwners(dynamic: true, key: copy.AssetSlot);
                    foreach (var slot in copy.OtherAssetSlots) { MeasureOwners(dynamic: true, key: slot); }
                }
            }
        }
        while (m_demandDue.TryPeek(element: out var group, priority: out var at) && (at < travel)) {
            _ = m_demandDue.Dequeue();
            if (group.DemandDueAt != at) { continue; }
            if (Measure(group: group)) { due++; }
        }
        if (m_demandSweepLeft > 0) {
            var count = m_groups.Count;
            var share = m_demandSweep.Share;
            var at = m_demandSweep.Start(count: count);
            var spent = 0L;

            for (var visited = 0; ((visited < count) && (spent < share) && (m_demandSweepLeft > 0)); visited++) {
                var group = m_groups[at];
                // A group another route measured since the reset began stands on the shape the reset asked for.
                var read = (((group.DemandMeasuredAt < m_demandResetAt) && Measure(group: group)) ? Math.Max(val1: 1, val2: (CopiesOf(group: group)?.Count ?? 1)) : 1);

                spent += read;
                m_demandSweepLeft--;
                at = m_demandSweep.Visited(at: at, boxes: read, count: count);
            }
            m_demandSweep.Stop(at: at);
        }
        // Entries the measures passed over pile up while groups move faster than the travel reaches them: the queue is
        // made again from the listed groups once it holds twice their number.
        if (m_demandDue.Count > ((2 * m_groups.Count) + 64)) {
            m_demandDue.Clear();
            foreach (var group in m_groups) {
                if (double.IsFinite(d: group.DemandDueAt) && !group.Retired) { m_demandDue.Enqueue(element: group, priority: group.DemandDueAt); }
            }
        }
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandGroups, amount: groups);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandBoxes, amount: boxes);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandDue, amount: due);

        void MeasureOwners(bool dynamic, int key) {
            for (var index = m_groups.FirstOf(dynamic: dynamic, key: key); (index >= 0); index = m_groups.NextOf(index: index)) { _ = Measure(group: m_groups[index]); }
        }
        // Measures one listed group, once a frame, and queues it at the travel its tier may next move at.
        bool Measure(Group group) {
            if (!group.Listed || group.Retired || !group.HasDemandBox || (group.DemandMeasuredAt == stamp)) { return false; }

            var before = group.DemandPixel;
            var pixel = MeasurePixel(boxes: out var read, group: group, point: out _, units: out var units);
            var slack = (DemandSlack(bias: bias, group: group, pixel: pixel) * units);

            group.DemandPixel = pixel;
            group.DemandMeasuredAt = stamp;
            group.DemandDueAt = (travel + slack);
            if (double.IsFinite(d: group.DemandDueAt)) { m_demandDue.Enqueue(element: group, priority: group.DemandDueAt); }
            if ((bias > 0) && (float.IsNaN(f: before) || (FinerTierOf(bias: bias, group: group, pixel: before) != FinerTierOf(bias: bias, group: group, pixel: pixel)))) { m_finerDemandRevision++; }
            Attend(group: group);
            groups++;
            boxes += read;
            return true;
        }
    }
    // The tier half a rung finer than the bias asks, held to the group's rungs: what the bias would demand one step back.
    private static int FinerTierOf(int bias, Group group, float pixel) =>
        SdfMeshTier.Clamp(coarsest: group.CoarsestTier, finest: group.FinestTier, tier: SdfMeshTier.Of(pixel: SdfMeshTier.Biased(halfRungs: (bias - 1), pixel: pixel)));
    // How far a group's pixel may move, in its own units, before the tier it demands under the bias changes, or the tier
    // half a rung finer does while the bias holds any (Retier's finer figure); infinity where no move can change either.
    private static float DemandSlack(int bias, Group group, float pixel) {
        if (!float.IsFinite(f: pixel)) { return float.PositiveInfinity; }

        var slack = SdfMeshTier.Slack(coarsest: group.CoarsestTier, finest: group.FinestTier, halfRungs: bias, pixel: pixel);

        return ((bias > 0) ? MathF.Min(x: slack, y: SdfMeshTier.Slack(coarsest: group.CoarsestTier, finest: group.FinestTier, halfRungs: (bias - 1), pixel: pixel)) : slack);
    }
    // Lists a group for the tier decisions to read, once.
    private void Attend(Group group) {
        if (group.Attended) { return; }
        group.Attended = true;
        m_attention.Add(item: group);
    }
}
