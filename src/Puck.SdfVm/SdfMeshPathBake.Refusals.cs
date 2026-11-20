using Puck.Abstractions.Memory;
using BakeGate = Puck.SdfVm.SdfMeshBakeReport.Gate;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // What a host memory refusal does to the bake: it defers the work it refused, and never fails a group. A batch's
    // answers never meet one (ReadbackBytes), a sample set the store keeps or reads past the account's room is dropped or
    // missed (SdfMeshStore.KeepSamples, TryReadSamples), a published copy waits (PublishMesh, ResumeParked), and any other
    // stage the account refuses starts its group's final lattice again under a larger admission (Readmit).

    // The meshes whose published copy the account refused, each with its group and the bytes the copy needs, waiting for
    // the group to reserve them (ResumeParked). Under the gate.
    private readonly List<(Group Group, SdfMesh Mesh, int Merged, long Bytes)> m_parked = [];

    // Parks a group's baked mesh whose published copy the account refused; the group stays at its landing, neither
    // offering a lattice nor done. A group the bake no longer holds lets the mesh go.
    private void ParkPublish(Group group, SdfMesh mesh, int merged, HostMemoryRefusedException refused) {
        var bytes = Math.Max(val1: refused.Bytes, val2: mesh.StorageBytes);

        using (EnterGate(site: BakeGate.Land)) {
            m_accounts.PublishWaits++;
            if (group.Retired || !GroupList.Holds(group: group)) { return; }
            m_parked.Add(item: (group, mesh, merged, bytes));
        }
        SdfMeshBakeReport.PublishDeferred(bytes: bytes, owner: refused.Owner, residency: Residency, selector: group.Selector);
    }
    // Publishes each parked mesh whose group can now reserve what it holds and the bytes its copy needs: the reservation
    // holds that room until the publish gives it to the copy's owners. Under the gate, on the take's thread.
    private void ResumeParked() {
        for (var index = (m_parked.Count - 1); (index >= 0); index--) {
            var (group, mesh, merged, bytes) = m_parked[index];

            if (group.Retired || !GroupList.Holds(group: group)) {
                m_parked.RemoveAt(index: index);
                continue;
            }
            if (!group.Memory.TryReserve(peak: (group.Memory.Bytes + bytes))) { continue; }
            m_parked.RemoveAt(index: index);
            SchedulePublish(group: group, mesh: mesh, merged: merged);
        }
    }
    private void SchedulePublish(Group group, SdfMesh mesh, int merged) => Schedule(group: group, work: () => PublishMesh(group: group, mesh: mesh, merged: merged));

    // Starts a group whose stage the account refused over from its final lattice, in place of failing it. The stage's
    // own workspace is freed here, on the job that ran it (no other job of the group runs), and the group then waits for
    // admission as any group starting its final lattice does, under a peak of at least what it held when refused and the
    // bytes refused (Group.PeakFloor), so the allocation that was refused fits its next admission: each refusal raises the
    // floor, and the group's next pass gets further.
    private void Readmit(Group group, HostMemoryRefusedException refused) {
        var floor = (group.Memory.Bytes + refused.Bytes);

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
        SdfMeshBakeReport.Readmitted(bytes: refused.Bytes, floor: floor, owner: refused.Owner, residency: Residency, selector: group.Selector);
    }
}
