using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly GpuRowIndex<int> m_rowsByFrameSlot = new();
    // Each slot a live draw rides, by slot; null for a slot no draw rides.
    private SlotMotion?[] m_slotMotion = [];
    private readonly List<int> m_dirtyMotionSlots = [];
    private int m_lastMotionSlotCount;

    private struct MotionBound {
        public SdfMeshBox? Box;
        public bool Live;
        public bool Glass;

        public void Include(in SdfMeshBox? box, bool glass) {
            if (!Live) { Box = box; }
            else if ((Box is { } held) && (box is { } next)) { Box = SdfMeshBox.Enclosing(in held, in next); }
            else { Box = null; }
            Live = true;
            Glass |= glass;
        }
    }

    private struct CarriedBound(SdfMeshRegion.SelectionCarry carry, in SdfMeshRegion.SlotSelection local) {
        public readonly SdfMeshRegion.SelectionCarry Carry = carry;
        public Vector3 Low = (local.Center * local.Scale) - Vector3.Abs(local.Half * local.Scale);
        public Vector3 High = (local.Center * local.Scale) + Vector3.Abs(local.Half * local.Scale);

        public void Include(in CarriedBound other) {
            Low = Vector3.Min(Low, other.Low);
            High = Vector3.Max(High, other.High);
        }
        public readonly SdfMeshBox At(in DynamicTransform transform) =>
            Carry.Place((Low + High) * 0.5f, (High - Low) * 0.5f, 1f, in transform);
        // A bound, in metres, on SdfWorldShadows' displacement between this box placed at one pose and at another: the
        // carry scales a point's motion, the translation moves every point alike, and the rotation's change moves a
        // point at distance r by at most its operator norm times r, which for unit quaternions is at most twice their
        // nearer distance. The displacement sums the centre's motion and each half axis's, so the radius is the
        // centre's distance from the slot's origin plus every half axis's length.
        public readonly float Displacement(in DynamicTransform from, in DynamicTransform to) {
            var center = (Low + High) * 0.5f;
            var half = Vector3.Abs((High - Low) * 0.5f);
            var radius = center.Length() + half.X + half.Y + half.Z;
            // The nearer of the two quaternions' distances, taken from their difference rather than their dot product, whose
            // cancellation would understate a small turn.
            var difference = (Quaternion.Dot(from.Orientation, to.Orientation) >= 0f) ? (from.Orientation - to.Orientation) : (from.Orientation + to.Orientation);
            var turn = 2f * difference.Length();

            return MathF.Abs(Carry.Scale) * (Vector3.Distance(from.Position, to.Position) + (turn * radius));
        }
    }

    private sealed class SlotMotion {
        public readonly List<CarriedBound> Carried = [];
        public MotionBound Fixed;
        public MotionBound Previous;
        public DynamicTransform Pose;
        public bool HasPose;
        public bool Glass;
        // Whether any member casts a shadow (SdfMeshRegion.CastsShadow): a slot none of whose members does owes the
        // shadows nothing as it moves.
        public bool Caster;
        public bool Dirty;
        public long Staging;
        // Previous was left at an earlier pose while the slot moved unplaced: it is placed again at Pose before it is
        // read.
        public bool PreviousStale;
        // The pose the slot's one carried box stood at when the shadows last judged it (SdfWorldShadows.Moved took the
        // box as its judged box), while HasJudgedPose holds.
        public DynamicTransform JudgedPose;
        public bool HasJudgedPose;
    }

    private static int MotionSlotOf(in SdfMeshDraw draw) =>
        ((draw.Mesh is { Members.IsEmpty: true }) && (draw.FrameSlot is >= 0 and { } slot)) ? slot : -1;

    private void DirtyMotionSlot(int slot) {
        Puck.Abstractions.Memory.ArrayCapacity.EnsureLength(array: ref m_slotMotion, required: (slot + 1));
        var motion = (m_slotMotion[slot] ??= new SlotMotion());

        if (motion.Dirty) { return; }
        motion.Dirty = true;
        m_dirtyMotionSlots.Add(slot);
    }

    // Only a draw-record change rebuilds a slot's union. Each carry is kept outside its local union since it is
    // applied after the slot's pose. A world-space record keeps its world box even when its material slot moves.
    private void RebuildMotionSlot(int slot, SlotMotion motion) {
        motion.Carried.Clear();
        motion.Fixed = default;
        motion.Glass = false;
        motion.Caster = false;
        var carries = motion.Carried;

        for (var row = m_rowsByFrameSlot.First(slot); row >= 0; row = m_rowsByFrameSlot.Next(row)) {
            var record = m_meshDrawWords.AsSpan(row * SdfMeshRecord.DrawWords, SdfMeshRecord.DrawWords);

            motion.Caster |= SdfMeshRegion.CastsShadow(record: record);
            if (SdfMeshRegion.TryReadSlotBox(record, out var local)) {
                var bound = new CarriedBound(local.Carry, in local);
                var index = 0;

                while ((index < carries.Count) && (carries[index].Carry != bound.Carry)) { index++; }
                if (index == carries.Count) { carries.Add(bound); }
                else {
                    var joined = carries[index];

                    joined.Include(in bound);
                    carries[index] = joined;
                }
                motion.Glass |= (record[SdfMeshRecord.SelectionWord + 3] & 2u) != 0u;
            } else if (SdfMeshRegion.ReadSelection(record, m_motionTransforms, out var box, out var glass)) {
                motion.Fixed.Include(in box, glass);
            }
        }
    }

    // A slot's judgement in two halves, the one door for a slot's scopes and shadows. Decide places the slot's boxes and
    // reads only the slot's own motion, the frame's poses, the draw records and the shadows' judged boxes, none of which
    // a judgement writes, so the moved slots of a staging decide side by side as the partitions of one fork. Settle hands
    // each decided change to the scopes and the shadows (NoteMotion), which every slot shares, so the slots settle on the
    // packing thread in the order they were collected, as one loop judged them before.
    private struct SlotJudgement {
        public int Slot;
        public SlotMotion Motion;
        public MotionBound Before;
        public MotionBound After;
        public DynamicTransform Pose;
        public bool HasPose;
        public bool PoseChanged;
        public bool Dirty;
        public bool Rigid;
        public bool Spared;
        public bool WasCaster;
        public bool IsCaster;
    }

    private SlotJudgement[] m_judgements = [];
    private int m_judgementCount;
    private SlotJudges? m_slotJudges;

    /// <summary>Gets or sets the pool the tables' pack forks its slot judgements across, or <see langword="null"/> to
    /// judge them on the packing thread. The residency sets it as it begins a pack (<see cref="SdfWorldResidency"/>).</summary>
    internal Puck.Hosting.Jobs.JobPool? Jobs { get; set; }

    // The moved slots a partition decides: enough that a fork pays for its wake, few enough that a staging of a few
    // thousand slots spreads across the workers.
    private const int JudgementsPerPartition = 128;

    // Adds a slot to the staging's judgements once a staging, in the order the loops reach it.
    private void CollectMotionSlot(int slot, SlotMotion motion) {
        if (motion.Staging == m_motionStaging) { return; }
        motion.Staging = m_motionStaging;
        Puck.Abstractions.Memory.ArrayCapacity.EnsureLength(array: ref m_judgements, required: (m_judgementCount + 1));
        m_judgements[m_judgementCount++] = new SlotJudgement { Motion = motion, Slot = slot };
    }

    // Decides every collected slot, side by side where the pool allows, then settles them in order.
    private void JudgeMotionSlots() {
        var count = m_judgementCount;

        if (count == 0) { return; }
        // Every entry past the count is written whole by the next collection before anything reads it, so the array is
        // never cleared; the slot motions it still names are the tables' own.
        m_judgementCount = 0;
        var partitions = Puck.Hosting.Jobs.PartitionRuns.Count(count: count, run: JudgementsPerPartition);

        if ((partitions > 1) && (Jobs is { Serial: false } pool)) {
            var judges = (m_slotJudges ??= new SlotJudges(owner: this));

            judges.Count = count;
            pool.Fork(body: judges, jobClass: Puck.Hosting.Jobs.JobClass.Frame, partitions: partitions);
        } else {
            for (var index = 0; (index < count); index++) { DecideMotionSlot(judgement: ref m_judgements[index]); }
        }
        var spared = 0L;

        for (var index = 0; (index < count); index++) {
            if (m_judgements[index].Spared) { spared++; } else { SettleMotionSlot(judgement: in m_judgements[index]); }
        }
        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.MotionRowsSpared, amount: spared);
    }

    private sealed class SlotJudges(SdfWorldTables owner) : Puck.Hosting.Jobs.IJobPartitions {
        public int Count;

        public void Run(int partition) {
            var (start, end) = Puck.Hosting.Jobs.PartitionRuns.Of(count: Count, partition: partition, run: JudgementsPerPartition);

            for (var index = start; (index < end); index++) {
                owner.DecideMotionSlot(judgement: ref owner.m_judgements[index]);
            }
        }
    }

    // The first half: places the slot's boxes under its pose and roster, both sides enclosing every live member, or spares
    // a rigid slot whose move the shadows would take as costing nothing. An unbounded member or a missing pose keeps the
    // whole side unbounded. Writes only the slot's own motion and its judgement.
    private void DecideMotionSlot(ref SlotJudgement judgement) {
        var slot = judgement.Slot;
        var motion = judgement.Motion;
        var dirty = motion.Dirty;
        var poses = m_motionTransforms;
        var hasPose = slot < poses.Count;
        var pose = hasPose ? poses[slot] : default;
        var poseChanged = (hasPose != motion.HasPose) ||
            (hasPose && ((pose.Position != motion.Pose.Position) || (pose.Orientation != motion.Pose.Orientation)));

        // With every scope met only the shadows read the change, and a rigid slot's box (one carried box, no fixed
        // member) is a function of its pose alone. An unmoved one is the box it was; a moved one whose pose change
        // bounds its box's displacement from the box the shadows last judged it by under their finest texel is a
        // change Moved would take as costing nothing, so neither is placed, nor a slot none of whose members casts a
        // shadow, which the shadows never judge. Moved measures against its judged box,
        // never the previous placement, so the drift gathers until it passes the texel. The open scope count is read as
        // the staging's judgements began, so a scope that a settle meets spares nothing more until the next staging.
        if (!dirty && (m_motionOpen == 0) && hasPose && motion.HasPose && !motion.Fixed.Live && (motion.Carried.Count == 1)) {
            var spared = !poseChanged || (m_shadows is null) || !motion.Caster ||
                (motion.HasJudgedPose && m_shadows.TryJudged(key: slot, box: out _) &&
                (motion.Carried[0].Displacement(from: in motion.JudgedPose, to: in pose) < StillMargin(m_shadows.StillDisplacement, in pose)));

            if (spared) {
                motion.PreviousStale |= poseChanged;
                motion.Pose = pose;
                judgement.Spared = true;
                return;
            }
        }
        if (motion.PreviousStale) {
            // Unplaced moves left the previous box at an earlier pose; the one carried box it held stands at the last.
            motion.PreviousStale = false;
            motion.Previous = default;
            motion.Previous.Include(motion.Carried[0].At(in motion.Pose), motion.Glass);
        }
        judgement.WasCaster = motion.Caster;
        if (dirty) { RebuildMotionSlot(slot, motion); }
        judgement.IsCaster = motion.Caster;
        var before = motion.Previous;
        var after = motion.Fixed;

        foreach (var bound in motion.Carried) {
            var box = hasPose ? bound.At(in pose) : (SdfMeshBox?)null;

            after.Include(in box, motion.Glass);
        }
        // Both sides are one box carried by the slot's pose alone, with no member added or gone between them: every caster
        // point then moves with the box, so the box's displacement bounds the shadow's, as it does a standalone row's.
        judgement.Rigid = !dirty && motion.HasPose && hasPose && !motion.Fixed.Live && (motion.Carried.Count == 1);
        (motion.Dirty, motion.Previous, motion.Pose, motion.HasPose) = (false, after, pose, hasPose);
        (judgement.Before, judgement.After, judgement.Pose, judgement.HasPose, judgement.PoseChanged, judgement.Dirty) = (before, after, pose, hasPose, poseChanged, dirty);
    }

    // The second half, on the packing thread in collection order: hands the decided change to the scopes and the
    // shadows, then lets the judged pose follow the shadows' judged box.
    private void SettleMotionSlot(in SlotJudgement judgement) {
        var slot = judgement.Slot;
        var motion = judgement.Motion;
        var (before, after) = (judgement.Before, judgement.After);

        if (!after.Live) { m_slotMotion[slot] = null; }
        if (!before.Live && !after.Live) { return; }

        SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.MotionRows);
        NoteMotion(cause: SdfMotionCause.Slot, row: slot, wasLive: before.Live, before: in before.Box, wasGlass: before.Glass,
            isLive: after.Live, after: in after.Box, isGlass: after.Glass, affectsShadows: judgement.Dirty || judgement.PoseChanged, rigid: judgement.Rigid,
            wasCaster: judgement.WasCaster, isCaster: judgement.IsCaster);

        // The judged pose follows the shadows' judged box: taken when Moved judged this one carried box, kept while
        // Moved left an earlier one standing (it measured within a texel of it), and dropped when the shadows hold none
        // or the slot's box is no single carried box.
        if (!judgement.HasPose || motion.Fixed.Live || (motion.Carried.Count != 1) || (m_shadows is null) || !m_shadows.TryJudged(key: slot, box: out var judged)) {
            motion.HasJudgedPose = false;
        } else if ((after.Box is { } placed) && judged.Equals(placed)) {
            (motion.JudgedPose, motion.HasJudgedPose) = (judgement.Pose, true);
        }
    }
    // The displacement under which a slot is left unplaced: a tenth under the shadows' own bound, less the float error
    // the two boxes' placements can carry at the slot's distance from the origin.
    private static float StillMargin(float stillDisplacement, in DynamicTransform pose) =>
        ((0.9f * stillDisplacement) - (8e-6f * pose.Position.Length()));

    private void NoteSlotMotion() {
        // A resized table can add or remove a pose without a row write at that slot.
        var count = m_motionTransforms.Count;
        if (count != m_lastMotionSlotCount) {
            var first = Math.Min(count, m_lastMotionSlotCount);
            var end = Math.Max(count, m_lastMotionSlotCount);

            for (var slot = first; (slot < Math.Min(val1: end, val2: m_slotMotion.Length)); slot++) {
                if (m_slotMotion[slot] is { Dirty: false } motion) {
                    motion.Dirty = true;
                    m_dirtyMotionSlots.Add(slot);
                }
            }
            m_lastMotionSlotCount = count;
        }
        foreach (var slot in m_dirtyMotionSlots) {
            if (m_slotMotion[slot] is { } dirty) { CollectMotionSlot(slot, dirty); }
        }
        m_dirtyMotionSlots.Clear();
        CollectMovedSlots(m_changedTransforms);
        CollectMovedSlots(m_reseatedTransforms);
        JudgeMotionSlots();
    }

    private void CollectMovedSlots(GpuUploadRuns slots) {
        for (var run = 0; run < slots.Count; run++) {
            var first = slots.Start(run);
            var end = Math.Min(m_packedTransformCount, first + slots.Length(run));

            for (var slot = first; slot < end; slot++) {
                if (m_motionOpen > 0) {
                    foreach (var (overlay, count, view, _) in m_overlayRows) {
                        if ((slot >= overlay) && ((slot - overlay) < count)) {
                            MeetEveryScope(cause: SdfMotionCause.Overlay, detail: slot, view: view);
                        }
                    }
                }
                if ((slot < m_slotMotion.Length) && (m_slotMotion[slot] is { } motion)) { CollectMotionSlot(slot, motion); }
            }
        }
    }
}
