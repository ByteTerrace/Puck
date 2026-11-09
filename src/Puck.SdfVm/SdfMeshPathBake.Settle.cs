using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // The presentation times of the latest settle rebakes this bake started, oldest first, for SdfMeshSettle.MaxPerSecond.
    private readonly float[] m_settleStarts = new float[SdfMeshSettle.MaxPerSecond];

    private int m_settleStartCount;

    // Watches every skinned dynamic group's members against the pose its mesh was baked at, and plans a group again at
    // its settled pose (SdfMeshSettle). A settle rebake that lands replaces its group's skinned mesh; one that meshes no
    // surface hands the group back, which keeps its mesh until its pose moves again. Runs under the gate, over the groups
    // whose geometry reads a slot besides their own alone (m_settleWatch), the only ones that drift or rebake.

    // The indices, ascending, of the groups whose geometry reads a slot besides their own: every group Settle watches and
    // every settle rebake, which keeps its slot. Rebuilt by Restart and kept by Replace, the only writers of m_groups.
    private readonly List<int> m_settleWatch = [];
    private readonly List<int> m_settleWatchScratch = [];

    // Lists every group that can drift, and every group that reads a sampled field, after Restart planned the groups.
    private void WatchSettles() {
        m_settleWatch.Clear();
        m_sampledReaders.Clear();
        for (var index = 0; (index < m_groups.Count); index++) {
            if (m_groups[index].ReadsOtherSlots) { m_settleWatch.Add(item: index); }
            if (m_groups[index].Geometry.ReadsSampled) { m_sampledReaders.Add(item: index); }
        }
    }
    // Keeps the settle watch as Replace puts a group at an index.
    private void WatchSettle(int index, Group group) {
        var at = m_settleWatch.BinarySearch(item: index);

        if (group.ReadsOtherSlots) {
            if (at < 0) { m_settleWatch.Insert(index: ~at, item: index); }
        } else if (at >= 0) {
            m_settleWatch.RemoveAt(index: at);
        }
    }
    private void Settle(SdfFrame frame) {
        if (m_settleWatch.Count == 0) { return; }

        var settles = Library.Settles;
        var inFlight = 0;

        // The settle rebakes still to land (FinishReplacements retires each replaced group as its replacement lands).
        foreach (var (group, _) in m_replacing) { inFlight += ((!group.Retiering && !group.Done) ? 1 : 0); }

        // Replace keeps the watch as it is for a group of the same slot, but a copy keeps the walk off it either way.
        var watched = m_settleWatchScratch;

        watched.Clear();
        watched.AddRange(collection: m_settleWatch);

        var transforms = frame.DynamicTransforms;

        foreach (var index in watched) {
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

        var (ownPosition, ownOrientation) = SdfMeshGeometryBuilder.PoseOf(transform: transforms[group.Slot]);
        var inverse = Quaternion.Inverse(value: ownOrientation);
        // The members' poses as the geometry keys them: in the group's own units.
        var units = SdfMeshGeometryBuilder.UnitsOf(transform: transforms[group.Slot]);
        var drifted = false;

        moved = !group.Seeded;
        for (var member = 0; (member < slots.Length); member++) {
            var (position, orientation) = SdfMeshGeometryBuilder.PoseOf(transform: transforms[slots[member]]);
            var relative = (Vector3.Transform(rotation: inverse, value: (position - ownPosition)) / units);
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
            instance: old.FirstInstance,
            sampled: m_sampled,
            tier: _ => old.Tier,
            transforms: frame.DynamicTransforms
        )!;

        Library.Settles.Count(kind: SdfMeshSettle.Queued);
        StartSettle(time: frame.Time);
        m_productRevision++;
        return StartReplacement(group: group, index: index, old: old);
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
    // The angle, in radians, one unit quaternion turns from another; q and -q turn alike.
    private static float Angle(Quaternion a, Quaternion b) =>
        (2f * MathF.Acos(x: MathF.Min(x: 1f, y: MathF.Abs(x: Quaternion.Dot(quaternion1: a, quaternion2: b)))));
}
