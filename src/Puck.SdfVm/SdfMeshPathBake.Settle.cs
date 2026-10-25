using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // The presentation times of the latest settle rebakes this bake started, oldest first, for SdfMeshSettle.MaxPerSecond.
    private readonly float[] m_settleStarts = new float[SdfMeshSettle.MaxPerSecond];

    private int m_settleStartCount;

    // Watches every skinned dynamic group's members against the pose its mesh was baked at, and plans a group again at
    // its settled pose (SdfMeshSettle). A settle rebake that lands replaces its group's skinned mesh; one that meshes no
    // surface hands the group back, which keeps its mesh until its pose moves again. Runs under the gate.
    private void Settle(SdfFrame frame) {
        var settles = Library.Settles;
        var inFlight = 0;

        for (var index = 0; (index < m_groups.Count); index++) {
            var group = m_groups[index];

            if (group.Replaces is not { } replaced) { continue; }
            if (!group.Done) {
                inFlight++;
                continue;
            }
            if (group.Mesh is null) {
                replaced.Blocked = true;
                Replace(group: replaced, index: index);
                m_drawnMeshes = -1;
                settles.Count(kind: SdfMeshSettle.Refused);
                continue;
            }
            group.Replaces = null;
            settles.Count(kind: SdfMeshSettle.Done);
            if (group.Stored) { settles.Count(kind: SdfMeshSettle.Reused); }
        }

        var transforms = frame.DynamicTransforms;

        for (var index = 0; (index < m_groups.Count); index++) {
            var group = m_groups[index];

            if (!group.Done || (group.Mesh is null) || !group.Dynamic || (group.Replaces is not null)) { continue; }
            if (!Drifted(group: group, moved: out var moved, time: frame.Time, transforms: transforms)) { continue; }
            if (moved || ((frame.Time - group.StillSince) < SdfMeshSettle.SettleSeconds)) {
                settles.Count(kind: SdfMeshSettle.Moving);
                continue;
            }
            if (group.Blocked) { continue; }
            if ((inFlight >= SdfMeshSettle.MaxInFlight) || !SettleRateAllows(time: frame.Time)) {
                settles.Count(kind: SdfMeshSettle.Capped);
                continue;
            }
            if (Rebake(frame: frame, index: index, old: group)) { inFlight++; }
        }
    }
    // Whether a group's members stand past SdfMeshSettle's drift from the pose its mesh was baked at; `moved` answers
    // whether any moved past its stillness from where the group last saw it change, in which case that becomes the pose
    // the group waits on. A group whose geometry reads no slot besides its own never drifts.
    private static bool Drifted(Group group, IReadOnlyList<DynamicTransform> transforms, float time, out bool moved) {
        moved = false;

        if (group.MemberSlots is null) { group.Watch(); }

        var slots = group.MemberSlots!;

        // The slots ascend, so the last is the highest the frame must hold.
        if ((slots.Length == 0) || (group.Slot >= transforms.Count) || (slots[^1] >= transforms.Count)) { return false; }

        var (ownPosition, ownOrientation) = PoseOf(transform: transforms[group.Slot]);
        var inverse = Quaternion.Inverse(value: ownOrientation);
        var drifted = false;

        moved = !group.Seeded;
        for (var member = 0; (member < slots.Length); member++) {
            var (position, orientation) = PoseOf(transform: transforms[slots[member]]);
            var relative = Vector3.Transform(rotation: inverse, value: (position - ownPosition));
            var turn = Quaternion.Normalize(value: (inverse * orientation));

            group.CurrentPositions![member] = relative;
            group.CurrentOrientations![member] = turn;
            moved |= ((Vector3.Distance(value1: relative, value2: group.StillPositions![member]) > SdfMeshSettle.StillPosition) ||
                (Angle(a: turn, b: group.StillOrientations![member]) > SdfMeshSettle.StillAngle));
            drifted |= ((Vector3.Distance(value1: relative, value2: group.BakedPositions![member]) > SdfMeshSettle.DriftPosition) ||
                (Angle(a: turn, b: group.BakedOrientations![member]) > SdfMeshSettle.DriftAngle));
        }
        if (moved) {
            group.CurrentPositions.AsSpan().CopyTo(destination: group.StillPositions);
            group.CurrentOrientations.AsSpan().CopyTo(destination: group.StillOrientations);
            group.StillSince = time;
            group.Seeded = true;
            group.Blocked = false;
        }

        return drifted;
    }
    // Plans a group again at its settled pose, snapped to SdfMeshSettle's steps, and starts it in the group's place: the
    // library's mesh of that geometry lands at once, and otherwise the group bakes (or loads from the store) while the
    // skinned mesh it replaces keeps drawing; true when the replacement is still to land. The slot rides the program the
    // group was planned from, so it plans a group, and a pose past the drift snaps away from the one the group baked at
    // (SdfMeshSettle.PositionSteps), so its geometry is another.
    private bool Rebake(SdfFrame frame, int index, Group old) {
        var group = SdfMeshBakeGroup.PlanSlot(
            create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
            farDistance: frame.FarDistance,
            orientationSteps: SdfMeshSettle.OrientationSteps,
            positionSteps: SdfMeshSettle.PositionSteps,
            program: frame.Program,
            slot: old.Slot,
            transforms: frame.DynamicTransforms
        )!;

        Library.Settles.Count(kind: SdfMeshSettle.Queued);
        StartSettle(time: frame.Time);
        group.Replaces = old;
        group.DrawnPosition = old.DrawnPosition;
        group.DrawnOrientation = old.DrawnOrientation;
        Replace(group: group, index: index);
        m_drawnMeshes = -1;
        if (Library.TryTake(geometry: group.Sealed, materials: group.MaterialOrder, mesh: out var baked)) {
            group.Mesh = baked;
            group.Done = true;
            group.Stored = true;
            return false;
        }

        group.Mesh = old.Mesh;
        _ = Begin(group: group);

        return true;
    }
    // Whether the bake may start another settle rebake at `time`: fewer than SdfMeshSettle.MaxPerSecond started in the
    // second before it. A time before the oldest start (a new frame source's clock) forgets the starts.
    private bool SettleRateAllows(float time) {
        if (m_settleStartCount < m_settleStarts.Length) { return true; }

        var oldest = m_settleStarts[0];

        if (time < oldest) {
            m_settleStartCount = 0;
            return true;
        }

        return ((time - oldest) >= 1f);
    }
    private void StartSettle(float time) {
        if (m_settleStartCount == m_settleStarts.Length) {
            Array.Copy(destinationArray: m_settleStarts, destinationIndex: 0, length: (m_settleStarts.Length - 1), sourceArray: m_settleStarts, sourceIndex: 1);
            m_settleStartCount--;
        }
        m_settleStarts[m_settleStartCount++] = time;
    }
    // A transform's position and orientation, an unset orientation read as the identity, as the bake reads it.
    private static (Vector3 Position, Quaternion Orientation) PoseOf(DynamicTransform transform) =>
        (transform.Position, ((transform.Orientation == default) ? Quaternion.Identity : transform.Orientation));
    // The angle, in radians, one unit quaternion turns from another; q and -q turn alike.
    private static float Angle(Quaternion a, Quaternion b) =>
        (2f * MathF.Acos(x: MathF.Min(x: 1f, y: MathF.Abs(x: Quaternion.Dot(quaternion1: a, quaternion2: b)))));
}
