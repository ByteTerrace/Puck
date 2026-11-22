using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The demand watch: which groups' demanded tiers a frame must measure again. A group's demanded tier is a function of
// its demand pixel (MeasurePixel) under the bias, and the pixel moves only when a demand point moves or changes, or the
// group's box moves. So a frame measures:
//   - the groups set into a place since the last frame (GroupList.TakeFresh), which the watch has never measured;
//   - the groups whose box moved: a dynamic group whose slot's transform moved, and a group one of whose copies moved
//     (a fixed copy through the copy journal, a moving one through its root slot in the moved transforms, since its pose
//     moves no revision), each found through the list's owner index (GroupList.FirstOf). A moved slot's group is
//     measured only when its world box moved, with the clocks since its measure, by as much as its bands leave (Moved);
//   - the groups a clock made due. SdfMeshDemand keeps two clocks no observation turns back, the points' travel and
//     scale, which bound how far any pixel moves. A measure leaves its group due on each at half its tier band's slack
//     (DemandWatch.Dues);
//   - the groups a frustum could have turned to hold or let go. A frustum bounded to its view answers whether it may
//     hold a box, and a measure keeps, for each such frustum whose answer could move the group's tier, how far the box
//     stood from the other answer and how far it reaches from that eye (SdfMeshDemand.Watch). Each frame reads every
//     watched group against each frustum's own shift and turn since (SdfMeshDemand.Moves): one frustum's sweep makes
//     due only the boxes near its own planes, never the boxes another, faster frustum passes far from;
//   - the groups a change of the points could reach (SdfMeshDemand.Changes: a point came, went, jumped or changed kind).
//     A box keeps its answer unless the changed point's distance alone puts its pixel under the box's band, so only the
//     boxes either side of the change reaches are measured, whatever the point set's size: at once where the new side
//     reaches (the pixel may fall), and a share a frame where only the old side does (it can only rise, asking a coarser
//     tier, which no decision takes before its hold). A new point bounded to its view demands nothing of a box its
//     frustum refuses, so such a box only begins watching that frustum (Unseen) and is measured once it may turn to it;
//   - and every group once, spread over SdfMeshTier.MeasureFrames frames, only when the points' changes overflowed their
//     list or before the first observation. A change of the bias recomputes every band from the pixels it holds, a lost
//     transform window reads every dynamic group's drift, and a lost copy window or a new copy layout measures the
//     shared geometries; none measures the rest.
// Invariant: a listed group's DemandPixel names the tier it stands at under the bias as of the frame's demand, except
// for a group a reset sweep has not reached yet, which is at most SdfMeshTier.MeasureFrames frames behind. Enforced by
// these routes being the only ones that can move a pixel: the clocks and changes are SdfMeshDemand's, the box routes
// read the frame's own journals, and every group enters the list through GroupList.Set. A change to what a pixel reads
// (a new input to MeasurePixel) must add its route here, or a group keeps a stale tier.
//
// The tier decisions read only the groups whose demanded tier a measure moved or the list set since they last stood
// (the attention list, Attend): a group whose demanded tier is the one it draws, with no first detail owed and no
// replacement in flight, has nothing a decision can do until its demanded or its drawn tier moves, and both pass
// through a measure, a rebias or a Set.
public sealed partial class SdfMeshPathBake {
    // The clocks a group falls due by, in SdfMeshDemand's order: Travel, Scale.
    private const int DemandClocks = 2;

    // What a group's latest measure stood on: the prepared frame it was taken at (zero for never), each clock as it read
    // then, the pixel its bounds are taken about and the band that pixel leaves, in the group's own units, and the units
    // it was read in (the fewest of a shared geometry's copies), the one world box it read (none for copies), the band's
    // top and how its boxes stood toward the points that could move its pixel past it (SdfMeshDemand.Bounds; the
    // frustums' part is the group's frustum watch), and the clock reading each clock makes it due at, with the epoch of
    // the queue that holds that due. The pixel
    // is the group's own (DemandPixel) where any point demands it; where none does, it is the least pixel a frustum-bound
    // point's distance puts it at, and its band the way down to the pixel that would move its tier
    // (SdfMeshTier.CoarsestEdge): only such a point turning to hold it could.
    private struct DemandWatch {
        public long Stamp;
        public DemandReadings At;
        public float Pixel;
        public float Slack;
        public float Units;
        public bool Boxed;
        public Vector3 Center;
        public Vector3 Half;
        public float Top;
        public float Focal;
        public float Clearance;
        public long DeferredAt;
        public DemandReadings Dues;
        public DemandEpochs Epochs;

        public readonly bool Measured => (Stamp != 0L);
    }
    [InlineArray(DemandClocks)]
    private struct DemandReadings { private double m_element; }
    [InlineArray(DemandClocks)]
    private struct DemandEpochs { private long m_element; }

    // The groups due on each clock, each at the reading it falls due at; an entry whose group was measured since (its
    // due moved) or left the list is passed over. Each clearing starts an epoch of that queue, and a group's due counts as
    // queued only when it was queued in the current one.
    private readonly PriorityQueue<Group, double>[] m_demandDue = [new(), new()];
    // The frustum watches of the groups whose tier a frustum bounded to its view could move, read each frame; the groups
    // a frustum's move made due this frame, or whose watch the lanes' layout left behind; and the watch a measure writes
    // before it keeps it.
    private readonly FrustumWatches m_watches = new();
    private readonly List<Group> m_framedDue = [];
    private float[] m_watchScratch = [];

    // The frustum watches (SdfMeshDemand.Watch), one record of SdfMeshDemand.WatchLength values a watched group, end to
    // end so a frame's read of them all streams through memory without touching the groups: each record's group (null
    // for a free record) and the prepared frame that group was last measured at, the free records, and the layout of
    // frustum lanes the records stand at (SdfMeshDemand.Layout). Group.FrustumWatch names a group's record.
    private sealed class FrustumWatches {
        private float[] m_values = [];
        private Group?[] m_owners = [];
        private long[] m_stamps = [];
        private readonly Stack<int> m_free = new();
        private int m_prune;

        // The values a record holds, the records ever taken (free ones among them), and the records held.
        public int Length { get; private set; }
        public int Count { get; private set; }
        public int Watched => (Count - m_free.Count);
        public long Layout { get; private set; } = -1;

        public Group? OwnerOf(int record) => m_owners[record];
        public long StampOf(int record) => m_stamps[record];
        public Span<float> Of(int record) => m_values.AsSpan(start: (record * Length), length: Length);

        // The record of a group measured at a stamp, taken for it when it holds none.
        public Span<float> Take(Group group, long stamp) {
            if (group.FrustumWatch < 0) { group.FrustumWatch = ((m_free.Count > 0) ? m_free.Pop() : Grow()); }

            var record = group.FrustumWatch;

            m_owners[record] = group;
            m_stamps[record] = stamp;
            return Of(record: record);
        }
        // Frees a group's record, when it holds one.
        public void Release(Group group) {
            if (group.FrustumWatch < 0) { return; }
            m_owners[group.FrustumWatch] = null;
            m_free.Push(item: group.FrustumWatch);
            group.FrustumWatch = -1;
        }
        // Frees the records of a share of groups that left the list or retired, a turn of the records at a time.
        public void Prune(int share) {
            for (var visited = 0; ((visited < share) && (Count > 0)); visited++) {
                m_prune = ((m_prune + 1) % Count);
                if ((m_owners[m_prune] is { } owner) && (!owner.Listed || owner.Retired)) { Release(group: owner); }
            }
        }
        // Brings every record to the demand's layout: carried from the layout before, freed where no lane is watched any
        // more, and freed with its group listed for a measure where the layout moved further than one change.
        public void Follow(SdfMeshDemand demand, List<Group> lost) {
            if (demand.Layout == Layout) { return; }

            var length = demand.WatchLength;
            var carries = (demand.Layout == (Layout + 1));
            var values = new float[(m_owners.Length * length)];

            for (var record = 0; (record < Count); record++) {
                if (m_owners[record] is not { } owner) { continue; }
                if (carries && demand.Carry(after: values.AsSpan(start: (record * length), length: length), before: Of(record: record))) { continue; }
                if (!carries) { lost.Add(item: owner); }
                Release(group: owner);
            }
            m_values = values;
            Length = length;
            Layout = demand.Layout;
        }
        private int Grow() {
            if (Count == m_owners.Length) {
                var capacity = Math.Max(val1: 64, val2: (2 * m_owners.Length));

                Array.Resize(array: ref m_owners, newSize: capacity);
                Array.Resize(array: ref m_stamps, newSize: capacity);
                Array.Resize(array: ref m_values, newSize: (capacity * Length));
            }

            return Count++;
        }
    }
    private readonly long[] m_demandEpochs = new long[DemandClocks];
    // The bias the watch measured under.
    private int m_demandBias = -1;
    // Whether a new copy layout asks the shared geometries to be measured again (IndexCopies).
    private bool m_demandCopyLayout;
    // How many groups the reset sweep still owes, where it stands, and the prepared frame the latest reset began at,
    // since which a group another route measured needs no measure from the sweep.
    private int m_demandSweepLeft;
    private GroupSweep m_demandSweep;
    private long m_demandResetAt;
    // The moved transforms the watch last read, and the slots they named.
    private readonly GpuRowCursor m_demandSlotCursor = new();
    private readonly GpuUploadRuns m_demandSlots = new(GpuRegion.MaxCopyRuns);
    // One more each time a measure moves a group's tier one half rung finer than the bias asks, which the bytes that
    // tier would name (Retier's finer figure) are cached against.
    private long m_finerDemandRevision;
    // This bake's measures since it began, by route, for its residency's report: every group measured, and those a moved
    // slot, a clock or a frustum, a frustum alone, a change of the points and a moved copy made.
    private (long Groups, long Moved, long Due, long Framed, long Changed, long Copied) m_demandMeasured;
    // The groups a change of the points could only coarsen, measured a share a frame (MeasureDemands).
    private readonly MarkedGroups m_demandDeferred = new(mark: GroupMarks.Deferred);
    // How a change of the points reaches a group (Reaches): not at all, only through points that left its reach (its
    // pixel can only rise), or through a point that came within it (its pixel may fall).
    private enum ChangeReach : byte { None, Coarser, Finer }
    // The groups the tier decisions read (Attend).
    private readonly MarkedGroups m_attention = new(mark: GroupMarks.Attended);

    // Measures again the groups whose demand may have moved since the last frame, as the header says. Under the gate,
    // on the frame's thread.
    private void MeasureDemands(SdfFrame frame) {
        var clocks = Clocks();
        var bias = Volatile.Read(location: ref m_bias);
        var changes = Demand.TakeChanges(overflowed: out var overflowed);
        var slotsWhole = m_demandSlotCursor.Collect(frame.MovedTransforms?.Changes, frame.MovedTransformsSerial, frame.DynamicTransforms,
            frame.DynamicTransforms.Count, 0, m_demandSlots);

        m_demandSlotCursor.Commit();
        m_framedDue.Clear();
        m_watches.Follow(demand: Demand, lost: m_framedDue);
        if (overflowed) {
            m_demandSweepLeft = m_groups.Count;
            m_demandResetAt = m_preparedFrames;
            for (var clock = 0; (clock < DemandClocks); clock++) { ClearQueue(clock: clock); }
            SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.DemandResets);
        }

        var stamp = m_preparedFrames;
        var reads = Demand.PointReads;
        Span<int> dueBy = stackalloc int[DemandClocks];
        var groups = 0;
        var boxes = 0L;
        var due = 0;
        var watched = 0;
        var framed = 0;
        var stood = 0;
        var moved = 0;
        var copied = 0;
        var reached = 0;
        var changed = 0;

        if (bias != m_demandBias) {
            m_demandBias = bias;
            Rebias(bias: bias);
        }
        foreach (var group in m_groups.TakeFresh()) { _ = Measure(group: group); }
        foreach (var group in m_framedDue) { _ = Measure(group: group); }
        // The copies' root slots follow the program's in copy order (SdfCompositionFrameSource seats each at its place), so
        // a moved slot past the first copy's root names that copy.
        var firstRoot = ((m_demandCopies.Count > 0) ? m_demandCopies[0].FrameSlot : int.MaxValue);

        if (slotsWhole) {
            // The window was lost: every dynamic group reads its own drift, as a moved slot's would.
            foreach (var group in m_groups) {
                if (!group.Dynamic) { continue; }
                if (!Moved(group: group)) { stood++; continue; }
                if (Measure(group: group)) { moved++; }
            }
        } else {
            for (var run = 0; (run < m_demandSlots.Count); run++) {
                var end = (m_demandSlots.Start(index: run) + m_demandSlots.Length(index: run));

                for (var slot = m_demandSlots.Start(index: run); (slot < end); slot++) {
                    for (var index = m_groups.FirstOf(dynamic: true, key: slot); (index >= 0); index = m_groups.NextOf(index: index)) {
                        var group = m_groups[index];

                        if (!Moved(group: group)) { stood++; continue; }
                        if (Measure(group: group)) { moved++; }
                    }
                    // A moving copy's pose moves its root slot and no revision, so the copy journal never names it: the
                    // shared geometry it carries is measured where it now stands.
                    if ((slot >= firstRoot) && ((slot - firstRoot) < m_demandCopies.Count) && (m_demandCopies[slot - firstRoot] is { IsDynamic: true } moving) && (moving.FrameSlot == slot)) {
                        MeasureCopy(copy: moving);
                    }
                }
            }
        }
        if (m_copiesWhole || m_demandCopyLayout) {
            // Every copy may have moved, or another layout seats them: the shared geometries, and those that were, are
            // measured where their copies now stand.
            m_demandCopyLayout = false;
            foreach (var group in m_groups) {
                if ((CopiesOf(group: group) is not null) || (group.Watch.Measured && !group.Watch.Boxed)) {
                    if (Measure(group: group)) { copied++; }
                }
            }
        } else {
            for (var run = 0; (run < m_copyRuns.Count); run++) {
                var end = Math.Min(val1: (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run)), val2: m_demandCopies.Count);

                for (var index = m_copyRuns.Start(index: run); (index < end); index++) { MeasureCopy(copy: m_demandCopies[index]); }
            }
        }
        if (!overflowed && (changes.Count > 0)) {
            SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandChanges, amount: changes.Count);
            foreach (var group in m_groups) {
                if (!group.Listed || group.Retired || !group.HasDemandBox || !group.Watch.Measured || (group.Watch.Stamp == stamp)) { continue; }
                reached++;

                var reach = Reaches(group: group);

                if (reach == ChangeReach.Finer) {
                    if (Measure(group: group)) { changed++; }
                } else if ((reach == ChangeReach.Coarser) && m_demandDeferred.Add(group: group)) {
                    group.Watch.DeferredAt = stamp;
                }
            }
        }
        for (var clock = 0; (clock < DemandClocks); clock++) {
            var queue = m_demandDue[clock];

            while (queue.TryPeek(element: out var group, priority: out var at) && (at < clocks[clock])) {
                _ = queue.Dequeue();
                if ((group.Watch.Dues[clock] != at) || (group.Watch.Epochs[clock] != m_demandEpochs[clock])) { continue; }
                if (Measure(group: group)) { due++; dueBy[clock]++; }
            }
        }
        // Every watched group against each of its frustums' own moves since its measure, the groups measured this frame
        // passed over; a group no frustum can move any more leaves the watch.
        if (m_watches.Watched > 0) {
            m_framedDue.Clear();
            for (var record = 0; (record < m_watches.Count); record++) {
                if ((m_watches.OwnerOf(record: record) is not { } owner) || (m_watches.StampOf(record: record) == stamp)) { continue; }
                watched++;
                if (Demand.Moves(metres: 0f, watch: m_watches.Of(record: record))) { m_framedDue.Add(item: owner); }
            }
            foreach (var group in m_framedDue) {
                if (!group.Listed || group.Retired) {
                    m_watches.Release(group: group);
                } else if (Measure(group: group)) {
                    due++;
                    framed++;
                }
            }
            m_watches.Prune(share: 64);
        }
        // The groups a change could only coarsen, a share a frame: a pixel that only rises asks a coarser tier, which the
        // decisions act on only past their hold, so a measure a few frames late moves no tier later than a reset's sweep
        // would have.
        if (m_demandDeferred.Count > 0) {
            var share = m_demandSweep.Share;
            var spent = 0L;
            var taken = 0;

            while ((taken < m_demandDeferred.Count) && (spent < share)) {
                var group = m_demandDeferred[taken++];

                spent += (((group.Watch.Stamp <= group.Watch.DeferredAt) && Measure(group: group)) ? Math.Max(val1: 1, val2: (CopiesOf(group: group)?.Count ?? 1)) : 1);
            }
            m_demandDeferred.Shift(count: taken);
        }
        if (m_demandSweepLeft > 0) {
            var count = m_groups.Count;
            var share = m_demandSweep.Share;
            var at = m_demandSweep.Start(count: count);
            var spent = 0L;

            for (var visited = 0; ((visited < count) && (spent < share) && (m_demandSweepLeft > 0)); visited++) {
                var group = m_groups[at];
                // A group another route measured since the reset began stands on the points the reset asked for.
                var read = (((group.Watch.Stamp < m_demandResetAt) && Measure(group: group)) ? Math.Max(val1: 1, val2: (CopiesOf(group: group)?.Count ?? 1)) : 1);

                spent += read;
                m_demandSweepLeft--;
                at = m_demandSweep.Visited(at: at, boxes: read, count: count);
            }
            m_demandSweep.Stop(at: at);
        }
        // Entries the measures passed over pile up while groups move faster than a clock reaches them: a queue is made
        // again from the listed groups once it holds twice their number.
        for (var clock = 0; (clock < DemandClocks); clock++) {
            if (m_demandDue[clock].Count <= ((2 * m_groups.Count) + 64)) { continue; }
            ClearQueue(clock: clock);
            foreach (var group in m_groups) {
                if (!group.Retired) { Queue(clock: clock, group: group); }
            }
        }
        m_demandMeasured.Groups += groups;
        m_demandMeasured.Moved += moved;
        m_demandMeasured.Due += due;
        m_demandMeasured.Framed += framed;
        m_demandMeasured.Changed += changed;
        m_demandMeasured.Copied += copied;
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandGroups, amount: groups);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandBoxes, amount: boxes);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandDue, amount: due);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandDueTravel, amount: dueBy[0]);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandDueScale, amount: dueBy[1]);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandWatched, amount: watched);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandFramed, amount: framed);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandViews, amount: (Demand.PointReads - reads));
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandStood, amount: stood);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandMoved, amount: moved);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandCopied, amount: copied);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandReached, amount: reached);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandChanged, amount: changed);

        // Measures the groups that carry a copy's geometry: its template range's, or its asset slots'.
        void MeasureCopy(SdfMeshInstance copy) {
            if (copy.TemplateRange is { } range) {
                for (var instance = 0; (instance < range.InstanceCount); instance++) { MeasureOwners(dynamic: false, key: (range.FirstInstance + instance)); }
                return;
            }
            MeasureOwners(dynamic: true, key: copy.AssetSlot);
            foreach (var slot in copy.OtherAssetSlots) { MeasureOwners(dynamic: true, key: slot); }
        }
        void MeasureOwners(bool dynamic, int key) {
            for (var index = m_groups.FirstOf(dynamic: dynamic, key: key); (index >= 0); index = m_groups.NextOf(index: index)) {
                if (Measure(group: m_groups[index])) { copied++; }
            }
        }
        // How a change of the points could move a measured group's tier: through a point it names whose distance from one
        // of the group's boxes, over its pixels a metre and in the group's units, puts it under the band's top (pixel plus
        // slack). The group's pixel is the least over the points, so a point whose pixel stays at or past that top was not
        // the least and does not become it; distance alone covers a frustum-bound point's every turn. A point that comes
        // under the top can make the pixel finer; one that only leaves can only make it coarser.
        ChangeReach Reaches(Group group) {
            var top = group.Watch.Top;
            var reach = ChangeReach.None;

            // A tier held at both of the group's rungs moves for no pixel.
            if (!float.IsFinite(f: top) && float.IsFinite(f: group.DemandPixel)) { return reach; }

            for (var index = 0; (index < changes.Count); index++) {
                var change = changes[index];

                if (change.HasAfter && (ReachOf(group: group, point: change.After) < top) && !Unseen(change: in change, group: group)) { return ChangeReach.Finer; }
                if (change.HadBefore && (ReachOf(group: group, point: change.Before) < top)) { reach = ChangeReach.Coarser; }
            }

            return reach;
        }
        // Whether a new point bounded to its view refuses every box of a group, so it demands nothing of it: the group then
        // watches the point's frustum from now on, as a measure would have it (SdfMeshDemand.WatchLane), and counts the
        // point's pixels a metre among those its box's moves are read by.
        bool Unseen(in SdfMeshDemand.Change change, Group group) {
            var point = change.After;

            if (!point.Within || !point.Framed || (change.Point < 0) || (Demand.LaneOf(point: change.Point) is not (>= 0 and var lane))) { return false; }

            var slack = float.PositiveInfinity;
            var reach = 0f;

            if (CopiesOf(group: group) is not { } copies) {
                var (center, half) = WorldBox(group: group);

                if (!FrustumRefuses(center: center, half: half, point: in point, reach: ref reach, slack: ref slack)) { return false; }
            } else {
                foreach (var copy in copies) {
                    var (center, half, _) = CopyBox(center: group.DemandCenter, copy: copy, half: group.DemandHalf);

                    if (!FrustumRefuses(center: center, half: half, point: in point, reach: ref reach, slack: ref slack)) { return false; }
                }
            }

            var fresh = (group.FrustumWatch < 0);
            var kept = m_watches.Take(group: group, stamp: group.Watch.Stamp);

            if (fresh) { SdfMeshDemand.Unwatched(watch: kept); }
            Demand.WatchLane(lane: lane, reach: reach, slack: slack, watch: kept);
            group.Watch.Focal = MathF.Min(x: group.Watch.Focal, y: point.Focal);
            return true;
        }
        // Whether a point's widened frustum refuses a box, and if so the box's slack toward it and reach from its eye,
        // each joined into the tightest so far.
        static bool FrustumRefuses(Vector3 center, Vector3 half, in SdfMeshDemand.Viewpoint point, ref float slack, ref float reach) {
            if (!point.Refuses(center: center, half: half, slack: out var away)) { return false; }
            slack = MathF.Min(x: slack, y: away);
            reach = MathF.Max(x: reach, y: (Vector3.Distance(value1: center, value2: point.Eye) + ((half.X + half.Y) + half.Z)));
            return true;
        }
        // Whether a group whose slot moved may demand another tier: its box moved by no more than the distance between the
        // world box its latest measure read and the one it stands at now, so its pixel moved by no more than that over the
        // points' fewest pixels a metre, with the points' own travel and scale since; and under its watched frustums its
        // distance to their planes moved by no more than that with each one's shift and turn. A group not measured since the
        // latest reset says it may. A shared geometry is measured where its copies stand, which the copy journal and their
        // root slots move, so its own slot moving says nothing.
        bool Moved(Group group) {
            ref readonly var watch = ref group.Watch;

            if (!group.Listed || group.Retired || !group.HasDemandBox || (watch.Stamp == stamp) || !watch.Measured || (watch.Stamp < m_demandResetAt)) { return true; }
            if (!watch.Boxed) { return false; }

            var (center, half) = WorldBox(group: group);
            var delta = Vector3.Abs(value: (half - watch.Half));
            var metres = (Vector3.Distance(value1: center, value2: watch.Center) + ((delta.X + delta.Y) + delta.Z));

            // A point that did not count could come to: the box moved as far as the nearest of them stood past the top.
            if (metres >= watch.Clearance) { return true; }
            if ((group.FrustumWatch >= 0) && Demand.Moves(metres: metres, watch: m_watches.Of(record: group.FrustumWatch))) { return true; }
            if (!float.IsFinite(f: watch.Slack)) { return false; }

            // In the group's own units: the world pixel moves by the travel and the box's drift over its new units, the
            // units' change moves the pixel it was measured at by their ratio (an animated slot's scale wavers), and the
            // points' scale multiplies the result.
            var units = UnitsOf(group: group);
            var pixel = watch.Pixel;
            var travel = (((((clocks[0] - watch.At[0]) + (metres / watch.Focal)) / units)) + (pixel * MathF.Abs(x: ((watch.Units / units) - 1f))));

            return ((((pixel + travel) * Math.Exp(d: (clocks[1] - watch.At[1]))) - pixel) >= watch.Slack);
        }
        // Measures one listed group, once a frame, and leaves it due on each clock at half what its bands leave there. A
        // group whose demanded tier stands is not read by the next decision for it.
        bool Measure(Group group) {
            if (!group.Listed || group.Retired || !group.HasDemandBox || (group.Watch.Stamp == stamp)) { return false; }

            var before = group.DemandPixel;
            var pixel = MeasurePixel(boxes: out var read, group: group, point: out _, units: out var units, box: out var box);
            var slack = DemandSlack(bias: bias, group: group, pixel: pixel);
            var anchor = pixel;
            ref var watch = ref group.Watch;
            // Only the points whose distance alone could put the group under its band's top (for a group no point demands,
            // under the pixel that would move its tier) bound it; a tier held at one rung moves for no point.
            var pinned = (float.IsFinite(f: pixel) && !float.IsFinite(f: slack));
            var top = (float.IsFinite(f: pixel) ? (pixel + slack) : CoarsestEdgeOf(bias: bias, group: group));
            var frustum = WatchScratch();
            var bounds = (pinned ? SdfMeshDemand.Bounds.None : BoundsOf(group: group, top: top, watch: frustum));

            if (!float.IsFinite(f: pixel) && float.IsFinite(f: top) && float.IsFinite(f: bounds.Beyond)) { (anchor, slack) = (bounds.Beyond, (MathF.Max(x: 0f, y: (bounds.Beyond - top)) * 0.999f)); }
            group.DemandPixel = pixel;
            watch.Stamp = stamp;
            watch.Pixel = anchor;
            watch.Slack = slack;
            watch.Units = units;
            watch.Boxed = (box is not null);
            (watch.Center, watch.Half) = (box ?? default);
            for (var clock = 0; (clock < DemandClocks); clock++) { watch.At[clock] = clocks[clock]; }
            (watch.Top, watch.Focal, watch.Clearance) = (top, bounds.Focal, bounds.Clearance);
            if (bounds.Framed) {
                var kept = m_watches.Take(group: group, stamp: stamp);

                frustum.CopyTo(destination: kept);
                Demand.Watch(watch: kept);
            } else {
                m_watches.Release(group: group);
            }
            Schedule(group: group, clocks: in clocks);
            if ((bias > 0) && (float.IsNaN(f: before) || (FinerTierOf(bias: bias, group: group, pixel: before) != FinerTierOf(bias: bias, group: group, pixel: pixel)))) { m_finerDemandRevision++; }
            if (float.IsNaN(f: before) || (DemandedTierOf(group: group, pixel: before) != DemandedTierOf(group: group, pixel: pixel))) { Attend(group: group); }
            groups++;
            boxes += read;
            return true;
        }
    }
    // Leaves a measured group due on each clock at half what its band leaves there, from the readings its measure took:
    // travel at half its tier band's slack in travel units, and scale where a pixel half that far past its band's top
    // would be scaled (with the travel's half, the two reach the slack together). An entry already
    // queued no later than the new due, in the current epoch and not yet passed, stays: popped, it measures again sooner
    // than needed.
    private void Schedule(Group group, in DemandReadings clocks) {
        ref var watch = ref group.Watch;
        var slack = watch.Slack;
        var pixel = watch.Pixel;
        Span<double> dues = [
            (float.IsFinite(f: slack) ? (watch.At[0] + (0.5 * slack * watch.Units)) : double.PositiveInfinity),
            ((float.IsFinite(f: slack) && float.IsFinite(f: pixel)) ? (watch.At[1] + Math.Log(d: (1.0 + (slack / ((2.0 * pixel) + slack))))) : double.PositiveInfinity),
        ];

        for (var clock = 0; (clock < DemandClocks); clock++) {
            var queued = watch.Dues[clock];

            if ((watch.Epochs[clock] == m_demandEpochs[clock]) && (queued <= dues[clock]) && (queued >= clocks[clock])) { continue; }
            watch.Dues[clock] = dues[clock];
            Queue(clock: clock, group: group);
        }
    }
    // Recomputes every measured group's bands under a new bias from the pixel it holds: no point moved, so no pixel did,
    // and only the tiers and slacks the pixels map to change. The queues start over from the new dues, and a group whose
    // demanded tier moved is read by the next decision.
    private void Rebias(int bias) {
        var clocks = Clocks();

        for (var clock = 0; (clock < DemandClocks); clock++) { ClearQueue(clock: clock); }
        foreach (var group in m_groups) {
            if (!group.Listed || group.Retired || !group.Watch.Measured) { continue; }
            // A group no point demands has its band from the bias's edge, which a measure this frame takes again.
            if (!float.IsFinite(f: group.DemandPixel)) {
                group.Watch.Dues[0] = (clocks[0] - 1.0);
                Queue(clock: 0, group: group);
                continue;
            }
            group.Watch.Slack = DemandSlack(bias: bias, group: group, pixel: group.DemandPixel);
            Schedule(group: group, clocks: in clocks);
            if (DemandedTierOf(group: group, pixel: group.DemandPixel) != group.DemandedTier) { Attend(group: group); }
        }
        m_finerDemandRevision++;
        SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.DemandRebiased);
    }
    // The pixel a point's distance alone puts a group's boxes at, in the group's units: its world box, or the nearest of a
    // shared geometry's copies.
    private float ReachOf(Group group, in SdfMeshDemand.Viewpoint point) {
        if (CopiesOf(group: group) is not { } copies) {
            var (center, half) = WorldBox(group: group);

            return (point.ReachAt(center: center, half: half) / UnitsOf(group: group));
        }

        var finest = float.PositiveInfinity;

        foreach (var index in copies) {
            var (center, half, scale) = CopyBox(center: group.DemandCenter, copy: index, half: group.DemandHalf);

            finest = MathF.Min(x: finest, y: (point.ReachAt(center: center, half: half) / scale));
        }

        return finest;
    }
    // How a group's boxes stand toward the points that could move its pixel past a top (SdfMeshDemand.BoundsOf), its
    // frustums' part written into its frustum watch: its world box's, or the tighter of each over a shared geometry's
    // copies, each read in its own units.
    private SdfMeshDemand.Bounds BoundsOf(Group group, float top, Span<float> watch) {
        if (CopiesOf(group: group) is not { } copies) {
            var (center, half) = WorldBox(group: group);

            return Demand.BoundsOf(center: center, half: half, top: top, units: UnitsOf(group: group), watch: watch);
        }

        var bounds = SdfMeshDemand.Bounds.None;

        foreach (var index in copies) {
            var (center, half, scale) = CopyBox(center: group.DemandCenter, copy: index, half: group.DemandHalf);

            bounds = bounds.Join(other: Demand.BoundsOf(center: center, half: half, top: top, units: scale, watch: watch));
        }

        return bounds;
    }
    // A frustum watch at the frustum lanes' stride, begun with no lane watched, for a measure to write.
    private Span<float> WatchScratch() {
        var length = Demand.WatchLength;

        if (m_watchScratch.Length != length) { m_watchScratch = new float[length]; }
        SdfMeshDemand.Unwatched(watch: m_watchScratch);
        return m_watchScratch;
    }
    // A shared geometry's box, given in its own frame, where one of the frame's copies carries it: the world-axis box about
    // the carried one, and the copy's scale, the units its pixels are read in.
    private (Vector3 Center, Vector3 Half, float Units) CopyBox(Vector3 center, Vector3 half, int copy) {
        var matrix = m_demandCopies[copy].Matrix;
        var (boxCenter, boxHalf) = TransformBox(center: center, half: half, matrix: matrix);

        return (boxCenter, boxHalf, MathF.Max(x: 1.0e-6f, y: new Vector3(x: matrix.M11, y: matrix.M12, z: matrix.M13).Length()));
    }
    // Queues a group at its due on a clock, in that queue's epoch, when any reading can make it due.
    private void Queue(int clock, Group group) {
        var due = group.Watch.Dues[clock];

        if (!group.Watch.Measured || !double.IsFinite(d: due)) { return; }
        group.Watch.Epochs[clock] = m_demandEpochs[clock];
        m_demandDue[clock].Enqueue(element: group, priority: due);
    }
    // Empties a clock's queue and starts its next epoch, so no group takes its old due as still queued.
    private void ClearQueue(int clock) {
        m_demandDue[clock].Clear();
        m_demandEpochs[clock]++;
    }
    // The clocks as the demand points hold them now, in DemandWatch's order.
    private DemandReadings Clocks() {
        var clocks = new DemandReadings();

        clocks[0] = Demand.Travel;
        clocks[1] = Demand.Scale;
        return clocks;
    }
    // The least pixel that still demands a group's coarsest held tier under the bias, or the tier half a rung finer while
    // the bias holds any (SdfMeshTier.CoarsestEdge): the larger of the two, so a point that could move either counts.
    private static float CoarsestEdgeOf(int bias, Group group) {
        var edge = SdfMeshTier.CoarsestEdge(coarsest: group.CoarsestTier, finest: group.FinestTier, halfRungs: bias);

        return ((bias > 0) ? MathF.Max(x: edge, y: SdfMeshTier.CoarsestEdge(coarsest: group.CoarsestTier, finest: group.FinestTier, halfRungs: (bias - 1))) : edge);
    }
    // The tier a group demands at a pixel under the bias, held to its rungs.
    private int DemandedTierOf(Group group, float pixel) => SdfMeshTier.Clamp(coarsest: group.CoarsestTier, finest: group.FinestTier, tier: TierOf(pixel: pixel));
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
    private void Attend(Group group) => _ = m_attention.Add(group: group);
}
