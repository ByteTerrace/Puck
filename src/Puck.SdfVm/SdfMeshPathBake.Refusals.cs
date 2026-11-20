using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
using BakeGate = Puck.SdfVm.SdfMeshBakeReport.Gate;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // What a host memory refusal does to the bake: it defers the work it refused, and never fails a group. A batch's
    // answers never meet one (ReadbackBytes), a sample set the store keeps or reads past the account's room is dropped or
    // missed (SdfMeshStore.KeepSamples, TryReadSamples), a copy into the library (a landing's published mesh, a follower's
    // renumbered take of its leader's) waits parked until there is room for it (Park, ResumeParked), and any other stage
    // the account refuses starts its group's final lattice again under a larger admission (Readmit).

    // The work whose copy into the library the account refused, each with its group, the bytes the copy needs and the
    // owner that reserves them in the copy's own allowance, waiting for room (ResumeParked). Under the gate.
    private readonly List<(Group Group, long Bytes, MemoryAccount.Owner Room, Action Work)> m_parked = [];

    // Parks a group's copy into the library that the account refused, to run again as `work` once `bytes` can be
    // reserved; the group waits where it stands, neither offering a lattice nor done. A group the bake no longer holds
    // lets the work go. The copy lands in the library's play storage (a mesh a prewarmed boot decodes into the same
    // owners), so its room is reserved as play's, apart from the group's authoring workspace.
    private void Park(Group group, long bytes, HostMemoryRefusedException refused, Action work) {
        using (EnterGate(site: BakeGate.Land)) {
            m_accounts.CopyWaits++;
            if (group.Retired || !GroupList.Holds(group: group)) { return; }
            m_parked.Add(item: (group, bytes, MemoryAccount.Shared.CreateOwner(memoryClass: MemoryClass.Workspace, name: "mesh-bake-copy"), work));
        }
        SdfMeshBakeReport.CopyDeferred(bytes: bytes, owner: refused.Owner, residency: Residency, selector: group.Selector);
    }
    // Runs again each parked copy whose bytes can now be reserved in play's allowance: the reservation holds that room
    // until the work, on the pool, gives it back for the copy's own owners to charge (SchedulePark). Under the gate, on
    // the take's thread, before the take admits any group.
    private void ResumeParked() {
        for (var index = (m_parked.Count - 1); (index >= 0); index--) {
            var (group, bytes, room, work) = m_parked[index];

            if (group.Retired || !GroupList.Holds(group: group)) {
                m_parked.RemoveAt(index: index);
                room.Dispose();
                continue;
            }
            if (!room.TryReserve(peak: bytes)) { continue; }
            m_parked.RemoveAt(index: index);
            SchedulePark(group: group, room: room, work: work);
        }
    }
    private void SchedulePark(Group group, MemoryAccount.Owner room, Action work) => Schedule(group: group, work: () => {
        room.Dispose();
        work();
    });

    // Starts a group whose stage the account refused over from its final lattice, in place of failing it. The stage's
    // own workspace is freed here, on the job that ran it (no other job of the group runs), and the group then waits for
    // admission as any group starting its final lattice does, under a peak of at least the most its storage has held and
    // the bytes refused (Group.PeakFloor): the refused allocation came on top of no more than that, so it fits the next
    // admission, each refusal raises the floor, and the group's next pass gets further.
    private void Readmit(Group group, HostMemoryRefusedException refused) {
        var floor = (group.Memory.HeldPeak + refused.Bytes);

        if (!group.Retired) {
            group.Final?.Dispose();
            group.Projection?.Dispose();
            group.Points.Resize(length: 0);
            group.PointMaterials.Resize(length: 0);
        }
        using (EnterGate(site: BakeGate.Fail)) {
            group.Contouring = false;
            group.InFlight = false;
            if (group.Done || !GroupList.Holds(group: group)) { return; }
            m_accounts.Readmissions++;
            group.Final = null;
            group.Contour = null;
            group.Cut = null;
            group.Creases = null;
            group.Projection = null;
            group.PeakFloor = Math.Max(val1: group.PeakFloor, val2: floor);
            group.Admitted = false;
            _ = group.TakeAdmittedAt();
            group.Memory.ReleaseReservation();
            StartFinal(group: group);
        }
        SdfMeshBakeReport.Readmitted(bytes: refused.Bytes, floor: floor, owner: refused.Owner, refusal: refused, residency: Residency, selector: group.Selector);
    }
}
