using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Waiting;
using Puck.Hosting.Jobs;
using Puck.SignedDistance;

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
    // a frustum's move made due this frame, or whose watch the lanes' layout left behind.
    private readonly FrustumWatches m_watches = new();
    private readonly List<Group> m_framedDue = [];

    // The frustum watches (SdfMeshDemand.Watch), one record of SdfMeshDemand.WatchLength values a watched group, end to
    // end so a frame's read of them all streams through memory without touching the groups: each record's group (null
    // for a free record) and the prepared frame that group was last measured at, the free records, and the layout of
    // frustum lanes the records stand at (SdfMeshDemand.Layout). Group.FrustumWatch names a group's record.
    private sealed class FrustumWatches {
        // The records' values at the current layout, and the buffer the next layout's records are carried into, kept
        // between layouts, since a layout moves whenever a frustum lane's point comes, goes or jumps. Either may be
        // longer than the records need. A reused buffer's old values are never read: a record is read only at its own
        // offset, only while owned, and Carry, a measure or Unseen writes the whole of it when it is taken.
        private float[] m_values = [];
        private float[] m_spare = [];
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
        // Lists the groups whose watch some lane's move since may have spent (SdfMeshDemand.Moves), the records of groups
        // measured at the stamp passed over; returns the records read.
        public int Trip(SdfMeshDemand demand, List<Group> due, long stamp) {
            var read = 0;

            due.Clear();
            for (var record = 0; (record < Count); record++) {
                if ((m_owners[record] is not { } owner) || (m_stamps[record] == stamp)) { continue; }
                read++;
                if (demand.Moves(metres: 0f, watch: m_values.AsSpan(start: (record * Length), length: Length))) { due.Add(item: owner); }
            }

            return read;
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
            var needed = (m_owners.Length * length);

            if (m_spare.Length < needed) { m_spare = new float[needed]; }

            var values = m_spare;

            for (var record = 0; (record < Count); record++) {
                if (m_owners[record] is not { } owner) { continue; }
                if (carries && demand.Carry(after: values.AsSpan(start: (record * length), length: length), before: Of(record: record))) { continue; }
                if (!carries) { lost.Add(item: owner); }
                Release(group: owner);
            }
            (m_spare, m_values) = (m_values, values);
            Length = length;
            Layout = demand.Layout;
        }
        private int Grow() {
            if (Count == m_owners.Length) {
                var capacity = Math.Max(val1: 64, val2: (2 * m_owners.Length));

                Array.Resize(array: ref m_owners, newSize: capacity);
                Array.Resize(array: ref m_stamps, newSize: capacity);
                if (m_values.Length < (capacity * Length)) { Array.Resize(array: ref m_values, newSize: (capacity * Length)); }
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
    // The moved transforms the frame last copied in (CopyFrameInputs), the slots they named, and whether those are every
    // slot because the journal could not prove the window.
    private readonly GpuRowCursor m_demandSlotCursor = new();
    private readonly GpuUploadRuns m_demandSlots = new(GpuRegion.MaxCopyRuns);
    private bool m_demandSlotsWhole;
    // What the measure and the bake's other demand reads take of the frame, in storage of the bake's own, since the
    // source rewrites the frame's at its next capture while the measure may still run: every dynamic slot's transform,
    // each copy's root matrix and frame slot (beside its layout, m_copyLayout), and the first copy's root slot.
    private DynamicTransform[] m_slotPoses = [];
    private CopyPose[] m_copyPoses = [];
    private int m_firstCopyRoot = int.MaxValue;

    private readonly record struct CopyPose(Matrix4x4 Matrix, int FrameSlot);

    // The measure's lane, which runs one measure a frame beside the rest of the frame (PostMeasure), whether a measure is
    // posted and not yet joined, and the gate both of those move under, which any thread may take to join.
    //
    // Invariant: while a measure is posted, nothing but the measure reads or writes the demand watch's state (the groups'
    // DemandPixel, Watch, FrustumWatch and demand marks, the attention list, the queues and frustum watches, and the
    // priority share's sweep and hole counts, Prioritize) or changes what it reads (the group list, the copy index, the
    // frame inputs copied above, the demand points). Enforced by the
    // joins at the doors that change them: PrepareFrame, the demand points' own doors (SdfMeshDemand.BeforeChange), the
    // reports that read the watch, and Retire; the group list throws on a change while frozen (GroupList.Frozen), and
    // DemandPixel throws when read or written off the measure while one is posted (ThrowIfMeasuring). The measure takes
    // no gate, so the frame's later entries (Pending, the take, ServeAhead, the dress) never wait on it. Workers write
    // none of the watch's state; the one group flag they move that the measure reads, Retired, only ever turns on, and a
    // measure that reads it late measures a retired group, which the next decision drops.
    private readonly Lock m_measureGate = new();
    private JobLane? m_measureLane;
    private DemandMeasure? m_measure;
    private bool m_measurePosted;
    private bool m_measureRetired;
    [ThreadStatic] private static bool ThreadMeasuring;

    private static readonly WaitReason MeasureWait = new(Subsystem: "sdf.bake", WaitsFor: "the demand measure");

    // One frame's demand measure and the groups' share of priorities (Prioritize), run on the bake's measure lane.
    private sealed class DemandMeasure(SdfMeshPathBake owner) : IJobLaneUnit {
        public void Run() {
            ThreadMeasuring = true;
            try {
                owner.MeasureDemands();
                owner.Prioritize();
            } finally { ThreadMeasuring = false; }
        }
    }

    // Hands the frame's demand measure to the measure lane once the frame's tier decision has read the one before; it
    // runs while the frame is dressed, packed and recorded, and the next PrepareFrame joins it. A retired bake measures
    // on the caller's thread. Under the gate, on the frame's thread.
    private void PostMeasure() {
        lock (m_measureGate) {
            if (m_measurePosted) { throw new InvalidOperationException(message: "A demand measure is posted already; PrepareFrame joins it before posting the next."); }
            if (Demand.BeforeChange is null) {
                Demand.BeforeChange = JoinMeasure;
            } else if (!ReferenceEquals(objA: Demand.BeforeChange.Target, objB: this)) {
                throw new InvalidOperationException(message: "Another bake measures by these demand points.");
            }
            if (m_measureRetired) {
                MeasureDemands();
                Prioritize();
                return;
            }
            m_measureLane ??= new JobLane(name: ("Puck demand measure: " + Residency), priority: JobPool.WorkerPriority);
            m_measureLane.Post(unit: (m_measure ??= new DemandMeasure(owner: this)));
            m_measurePosted = true;
            m_groups.Frozen = true;
        }
    }
    // Waits for the posted demand measure, if any, and takes it: what it read may change and what it wrote may be read
    // once this returns. Rethrows what the measure threw. Any thread.
    private void JoinMeasure() {
        lock (m_measureGate) {
            if (!m_measurePosted) { return; }

            var lane = m_measureLane!;
            var outcome = WaitOutcome.DeadlinePassed;

            if (!lane.Ready) { SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.DemandJoinWaits); }
            try {
                outcome = lane.Collect(deadline: WaitDeadline.Synchronous(), reason: in MeasureWait);
            } finally {
                // A measure that threw is collected too: the lane is free, and the throw reaches the caller.
                if ((outcome == WaitOutcome.Completed) || !lane.InFlight) {
                    m_measurePosted = false;
                    m_groups.Frozen = false;
                }
            }
            if (outcome == WaitOutcome.DeadlinePassed) { HostWaits.ThrowIfPending(pending: MeasureWait); }
        }
    }
    // Joins the measure and stops its lane for a bake retired: a frame prepared after measures on its own thread.
    private void RetireMeasure() {
        JoinMeasure();
        lock (m_measureGate) {
            m_measureRetired = true;
            m_measureLane?.Dispose();
            m_measureLane = null;
        }
    }
    // Throws when the demand watch's state is touched off the measure while a measure is posted (the invariant above).
    private void ThrowIfMeasuring() {
        if (m_groups.Frozen && !ThreadMeasuring) { throw new InvalidOperationException(message: "The demand watch is read and written only by the posted measure until PrepareFrame joins it."); }
    }

    // Copies what the demand reads of the frame into the bake's own (m_slotPoses, m_copyPoses), in proportion to what
    // moved: the slots the moved-transform journal names since the last prepared frame, and the copies the copy journal
    // names or whose root slot moved; every slot or copy when a journal cannot prove its window or the layout is new.
    // Under the gate, on the frame's thread, after the copy layout is compared (SameCopyLayout) and indexed.
    private void CopyFrameInputs(SdfFrame frame, bool reindexed) {
        var transforms = frame.DynamicTransforms;
        var slots = transforms.Count;
        var slotsWhole = m_demandSlotCursor.Collect(frame.MovedTransforms?.Changes, frame.MovedTransformsSerial, transforms, slots, 0, m_demandSlots);

        m_demandSlotCursor.Commit();
        m_demandSlotsWhole = slotsWhole;
        if (m_slotPoses.Length != slots) {
            m_slotPoses = ((slots == 0) ? [] : new DynamicTransform[slots]);
            slotsWhole = true;
        }
        if (slotsWhole) {
            CopySlots(start: 0, end: slots);
        } else {
            for (var run = 0; (run < m_demandSlots.Count); run++) {
                CopySlots(start: m_demandSlots.Start(index: run), end: Math.Min(val1: slots, val2: (m_demandSlots.Start(index: run) + m_demandSlots.Length(index: run))));
            }
        }

        var copies = frame.MeshInstances;
        var count = copies.Count;
        var copiesWhole = (reindexed || m_copiesWhole || (m_copyPoses.Length != count));

        if (m_copyPoses.Length != count) { m_copyPoses = ((count == 0) ? [] : new CopyPose[count]); }
        m_firstCopyRoot = ((count > 0) ? copies[0].FrameSlot : int.MaxValue);
        if (count == 0) { return; }
        if (copiesWhole) {
            CopyCopies(start: 0, end: count);
            return;
        }
        for (var run = 0; (run < m_copyRuns.Count); run++) {
            CopyCopies(start: m_copyRuns.Start(index: run), end: Math.Min(val1: count, val2: (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run))));
        }
        // A moving copy's pose moves its root slot and no revision, so the copy journal never names it.
        if (slotsWhole) {
            CopyCopies(start: 0, end: count);
            return;
        }
        for (var run = 0; (run < m_demandSlots.Count); run++) {
            var first = Math.Max(val1: m_demandSlots.Start(index: run), val2: m_firstCopyRoot);
            var end = Math.Min(val1: (m_demandSlots.Start(index: run) + m_demandSlots.Length(index: run)), val2: (m_firstCopyRoot + count));

            CopyCopies(start: (first - m_firstCopyRoot), end: (end - m_firstCopyRoot));
        }

        void CopySlots(int start, int end) {
            if (end <= start) { return; }
            if (transforms is DynamicTransform[] array) {
                array.AsSpan(start: start, length: (end - start)).CopyTo(destination: m_slotPoses.AsSpan(start: start));
                return;
            }
            for (var slot = start; (slot < end); slot++) { m_slotPoses[slot] = transforms[slot]; }
        }
        void CopyCopies(int start, int end) {
            for (var copy = start; (copy < end); copy++) {
                var instance = copies[copy];

                m_copyPoses[copy] = new CopyPose(FrameSlot: instance.FrameSlot, Matrix: instance.Matrix);
            }
        }
    }
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
    // The groups the frame's routes chose to measure, each once (MeasureDemands' Measure), with each one's readings and
    // frustum watch as MeasureChosen takes them before it keeps them; the bias and watch length they are taken under.
    private readonly List<Group> m_chosen = [];
    private DemandReading[] m_readings = [];
    private float[] m_chosenWatches = [];
    private int m_chosenBias;
    private int m_chosenLength;


    // What one measure read of a group (MeasureDemands' Measure): its pixel, its band's slack and the pixel that band is
    // taken about, the units and boxes read, the one world box it read, its band's top and how its boxes stood toward the
    // points (its frustums' part in the frustum watch beside it).
    private struct DemandReading {
        public float Pixel;
        public float Slack;
        public float Anchor;
        public float Units;
        public float Top;
        public int Boxes;
        public bool Boxed;
        public Vector3 Center;
        public Vector3 Half;
        public SdfMeshDemand.Bounds Bounds;
    }

    // Measures again the groups whose demand may have moved since the frame before, as the header says, from the inputs
    // the frame copied into the bake's own (CopyFrameInputs). On the measure's lane, outside the gate (PostMeasure).
    private void MeasureDemands() {
        var clocks = Clocks();
        var bias = Volatile.Read(location: ref m_bias);
        var changes = Demand.TakeChanges(overflowed: out var overflowed);
        var slotsWhole = m_demandSlotsWhole;

        m_framedDue.Clear();
        m_watches.Follow(demand: Demand, lost: m_framedDue);
        if (overflowed) {
            m_demandSweepLeft = m_groups.Count;
            m_demandResetAt = m_preparedFrames;
            for (var clock = 0; (clock < DemandClocks); clock++) { ClearQueue(clock: clock); }
            SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.DemandResets);
        }

        var stamp = m_preparedFrames;
        Span<int> dueBy = stackalloc int[DemandClocks];
        var groups = 0;
        var boxes = 0L;
        var due = 0;
        var watched = 0;
        var framed = 0;
        var renewed = 0;
        var slots = 0L;
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
        var firstRoot = m_firstCopyRoot;
        var copyCount = m_copyPoses.Length;

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

                slots += (end - m_demandSlots.Start(index: run));
                for (var slot = m_demandSlots.Start(index: run); (slot < end); slot++) {
                    for (var index = m_groups.FirstOf(dynamic: true, key: slot); (index >= 0); index = m_groups.NextOf(index: index)) {
                        var group = m_groups[index];

                        if (!Moved(group: group)) { stood++; continue; }
                        if (Measure(group: group)) { moved++; }
                    }
                    // A moving copy's pose moves its root slot and no revision, so the copy journal never names it: the
                    // shared geometry it carries is measured where it now stands.
                    if ((slot >= firstRoot) && ((slot - firstRoot) < copyCount) && m_copyLayout[slot - firstRoot].IsDynamic && (m_copyPoses[slot - firstRoot].FrameSlot == slot)) {
                        MeasureCopy(copy: (slot - firstRoot));
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
                var end = Math.Min(val1: (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run)), val2: copyCount);

                for (var index = m_copyRuns.Start(index: run); (index < end); index++) { MeasureCopy(copy: index); }
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
            watched += m_watches.Trip(demand: Demand, due: m_framedDue, stamp: stamp);
            foreach (var group in m_framedDue) {
                if (!group.Listed || group.Retired) {
                    m_watches.Release(group: group);
                } else if ((group.Watch.Stamp != stamp) && (CopiesOf(group: group) is null) && Renewed(group: group)) {
                    renewed++;
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
        MeasureChosen(groups: ref groups, boxes: ref boxes, bias: bias, clocks: in clocks, stamp: stamp);
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
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandRenewed, amount: renewed);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandSlots, amount: slots);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandViews, amount: (boxes * Demand.Count));
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandStood, amount: stood);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandMoved, amount: moved);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandCopied, amount: copied);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandReached, amount: reached);
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.DemandChanged, amount: changed);

        // Measures the groups that carry a copy's geometry: its template range's, or its asset slots'.
        void MeasureCopy(int copy) {
            var layout = m_copyLayout[copy];

            if (layout.TemplateRange is { } range) {
                for (var instance = 0; (instance < range.InstanceCount); instance++) { MeasureOwners(dynamic: false, key: (range.FirstInstance + instance)); }
                return;
            }
            MeasureOwners(dynamic: true, key: layout.AssetSlot);
            foreach (var slot in layout.OtherAssetSlots) { MeasureOwners(dynamic: true, key: slot); }
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
        // Whether a group's moved frustums all still answer as they did at its box as it stands, renewing their watch
        // (SdfMeshDemand.Renew), so it needs no measure.
        bool Renewed(Group group) {
            var (center, half) = WorldBox(group: group);

            return Demand.Renew(center: center, half: half, watch: m_watches.Of(record: group.FrustumWatch));
        }
        // Chooses one listed group to measure this frame, once: its readings are taken with the rest of the frame's after
        // every route has chosen (MeasureChosen), and from now on the frame's routes read it as measured.
        bool Measure(Group group) {
            if (!group.Listed || group.Retired || !group.HasDemandBox || (group.Watch.Stamp == stamp)) { return false; }
            group.Watch.Stamp = stamp;
            m_chosen.Add(item: group);
            return true;
        }
    }
    // Measures the groups the frame's routes chose: each one's readings first (ReadChosen), which read only its own group,
    // the points and the frame's boxes and write only its own reading, then each kept in order (KeepReading).
    private void MeasureChosen(ref int groups, ref long boxes, int bias, in DemandReadings clocks, long stamp) {
        var count = m_chosen.Count;

        if (count == 0) { return; }

        var length = Demand.WatchLength;

        if (m_readings.Length < count) {
            m_readings = new DemandReading[ArrayCapacity.Grown(m_readings.Length, count)];
        }
        if (m_chosenWatches.Length < (m_readings.Length * length)) { m_chosenWatches = new float[(m_readings.Length * length)]; }
        (m_chosenBias, m_chosenLength) = (bias, length);
        for (var index = 0; (index < count); index++) { ReadChosen(index: index); }
        for (var index = 0; (index < count); index++) {
            KeepReading(clocks: in clocks, group: m_chosen[index], index: index, stamp: stamp);
            boxes += m_readings[index].Boxes;
        }
        groups += count;
        m_chosen.Clear();
    }
    // Takes one chosen group's readings: its pixel, the band it leaves under the bias, and how its boxes stand toward the
    // points that could move it past the band's top. Only the points whose distance alone could put the group under its
    // band's top (for a group no point demands, under the pixel that would move its tier) bound it; a tier held at one
    // rung moves for no point.
    private void ReadChosen(int index) {
        var group = m_chosen[index];
        var bias = m_chosenBias;
        ref var reading = ref m_readings[index];
        var copies = CopiesOf(group: group);
        (Vector3 Center, Vector3 Half)? box = null;
        float pixel;

        if (copies is null) {
            var (center, half) = WorldBox(group: group);

            box = (center, half);
            (reading.Boxes, reading.Units) = (1, UnitsOf(group: group));
            pixel = (Demand.PixelAt(center: center, half: half) / reading.Units);
        } else {
            reading.Boxes = Math.Max(val1: 1, val2: copies.Count);
            pixel = CopiesPixel(center: group.DemandCenter, copies: copies, half: group.DemandHalf, point: out _, units: out reading.Units);
        }

        var slack = DemandSlack(bias: bias, group: group, pixel: pixel);
        var pinned = (float.IsFinite(f: pixel) && !float.IsFinite(f: slack));
        var top = (float.IsFinite(f: pixel) ? (pixel + slack) : CoarsestEdgeOf(bias: bias, group: group));
        var watch = m_chosenWatches.AsSpan(start: (index * m_chosenLength), length: m_chosenLength);
        var bounds = SdfMeshDemand.Bounds.None;

        SdfMeshDemand.Unwatched(watch: watch);
        if (!pinned && (box is { } one)) {
            bounds = Demand.BoundsOf(center: one.Center, half: one.Half, top: top, units: reading.Units, watch: watch);
        } else if (!pinned && (copies is not null)) {
            // The copies CopiesPixel read and held, each in its own scale.
            for (var copy = 0; (copy < copies.Count); copy++) { bounds = bounds.Join(other: Demand.HeldBoundsOf(box: copy, top: top, units: m_copyScales[copy], watch: watch)); }
        }
        reading.Anchor = pixel;
        if (!float.IsFinite(f: pixel) && float.IsFinite(f: top) && float.IsFinite(f: bounds.Beyond)) { (reading.Anchor, slack) = (bounds.Beyond, (MathF.Max(x: 0f, y: (bounds.Beyond - top)) * 0.999f)); }
        reading.Pixel = pixel;
        reading.Slack = slack;
        reading.Top = top;
        reading.Boxed = (box is not null);
        (reading.Center, reading.Half) = (box ?? default);
        reading.Bounds = bounds;
    }
    // Keeps one chosen group's readings, on the frame's thread: its watch stands on them, it falls due on each clock at
    // half what its band leaves there, its frustums are watched, and a group whose demanded tier stands is not read by
    // the next decision for it.
    private void KeepReading(Group group, int index, in DemandReadings clocks, long stamp) {
        ref readonly var reading = ref m_readings[index];
        ref var watch = ref group.Watch;
        var bias = m_chosenBias;
        var before = group.DemandPixel;
        var pixel = reading.Pixel;

        group.DemandPixel = pixel;
        watch.Pixel = reading.Anchor;
        watch.Slack = reading.Slack;
        watch.Units = reading.Units;
        watch.Boxed = reading.Boxed;
        (watch.Center, watch.Half) = (reading.Center, reading.Half);
        for (var clock = 0; (clock < DemandClocks); clock++) { watch.At[clock] = clocks[clock]; }
        (watch.Top, watch.Focal, watch.Clearance) = (reading.Top, reading.Bounds.Focal, reading.Bounds.Clearance);
        if (reading.Bounds.Framed) {
            var kept = m_watches.Take(group: group, stamp: stamp);

            m_chosenWatches.AsSpan(start: (index * m_chosenLength), length: m_chosenLength).CopyTo(destination: kept);
            Demand.Watch(watch: kept);
        } else {
            m_watches.Release(group: group);
        }
        Schedule(group: group, clocks: in clocks);
        if ((bias > 0) && (float.IsNaN(f: before) || (FinerTierOf(bias: bias, group: group, pixel: before) != FinerTierOf(bias: bias, group: group, pixel: pixel)))) { m_finerDemandRevision++; }
        if (float.IsNaN(f: before) || (DemandedTierOf(group: group, pixel: before) != DemandedTierOf(group: group, pixel: pixel))) { Attend(group: group); }
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
    // A shared geometry's box, given in its own frame, where one of the frame's copies carries it: the world-axis box about
    // the carried one, and the copy's scale, the units its pixels are read in.
    private (Vector3 Center, Vector3 Half, float Units) CopyBox(Vector3 center, Vector3 half, int copy) {
        var matrix = m_copyPoses[copy].Matrix;
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
