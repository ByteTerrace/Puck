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
    }

    private sealed class SlotMotion {
        public readonly List<CarriedBound> Carried = [];
        public MotionBound Fixed;
        public MotionBound Previous;
        public DynamicTransform Pose;
        public bool HasPose;
        public bool Glass;
        public bool Dirty;
        public long Staging;
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
        var carries = motion.Carried;

        for (var row = m_rowsByFrameSlot.First(slot); row >= 0; row = m_rowsByFrameSlot.Next(row)) {
            var record = m_meshDrawWords.AsSpan(row * SdfMeshRecord.DrawWords, SdfMeshRecord.DrawWords);

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

    // One door for a slot's scopes and shadows. Both sides enclose every live member under the corresponding pose
    // and roster. An unbounded member or a missing pose keeps the whole side unbounded.
    private void JudgeMotionSlot(int slot, SlotMotion motion) {
        if (motion.Staging == m_motionStaging) { return; }
        motion.Staging = m_motionStaging;
        var dirty = motion.Dirty;

        if (dirty) { RebuildMotionSlot(slot, motion); }
        var hasPose = slot < m_motionTransforms.Count;
        var pose = hasPose ? m_motionTransforms[slot] : default;
        var poseChanged = (hasPose != motion.HasPose) ||
            (hasPose && ((pose.Position != motion.Pose.Position) || (pose.Orientation != motion.Pose.Orientation)));
        var before = motion.Previous;
        var after = motion.Fixed;

        foreach (var bound in motion.Carried) {
            var box = hasPose ? bound.At(in pose) : (SdfMeshBox?)null;

            after.Include(in box, motion.Glass);
        }
        // Both sides are one box carried by the slot's pose alone, with no member added or gone between them: every caster
        // point then moves with the box, so the box's displacement bounds the shadow's, as it does a standalone row's.
        var rigid = !dirty && motion.HasPose && hasPose && !motion.Fixed.Live && (motion.Carried.Count == 1);

        (motion.Dirty, motion.Previous, motion.Pose, motion.HasPose) = (false, after, pose, hasPose);
        if (!after.Live) { m_slotMotion[slot] = null; }
        if (!before.Live && !after.Live) { return; }

        SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.MotionRows);
        NoteMotion(cause: SdfMotionCause.Slot, row: slot, wasLive: before.Live, before: in before.Box, wasGlass: before.Glass,
            isLive: after.Live, after: in after.Box, isGlass: after.Glass, affectsShadows: dirty || poseChanged, rigid: rigid);
    }

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
            if (m_slotMotion[slot] is { } dirty) { JudgeMotionSlot(slot, dirty); }
        }
        m_dirtyMotionSlots.Clear();
        NoteMovedSlots(m_changedTransforms);
        NoteMovedSlots(m_reseatedTransforms);
    }

    private void NoteMovedSlots(GpuUploadRuns slots) {
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
                if ((slot < m_slotMotion.Length) && (m_slotMotion[slot] is { } motion)) { JudgeMotionSlot(slot, motion); }
            }
        }
    }
}
