using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly List<IndirectMeshGeometry> m_indirectMeshes = [];

    private long m_indirectMeshRevision;
    private long m_indirectMeshesStagedRevision = -1;
    private SdfIndirectParticipation m_indirectMeshesBodies;

    internal bool HasUncertifiedIndirectMesh => m_indirectMeshes.Any(predicate: static draw => !draw.FieldBacked);

    // Copy the caster identities and poses: emitters may rewrite their existing draw list in place. Receiver-only
    // motion still updates the ordinary mesh region, without invalidating a depth map that never included that draw.
    private void StageIndirectMeshes(IReadOnlyList<SdfMeshDraw> draws) {
        if ((m_indirectMeshesStagedRevision == m_meshRevision) && (m_indirectMeshesBodies == m_indirectBodies)) { return; }
        m_indirectMeshesStagedRevision = m_meshRevision;
        m_indirectMeshesBodies = m_indirectBodies;
        var count = 0;
        var changed = false;

        foreach (var draw in draws) {
            if (SdfIndirectPolicy.Resolve(draw.EffectiveIndirectParticipation(instancesComposable: m_meshIndirectInstancesComposable), draw.IsDynamic,
                SdfIndirectTier.Off, m_indirectBodies) != SdfIndirectParticipation.Cast) { continue; }
            var geometry = new IndirectMeshGeometry(draw.Mesh, draw.ObjectToWorld, draw.Identity, draw.FieldBacked, draw.Lod, draw.Impostor);

            if (count == m_indirectMeshes.Count) { m_indirectMeshes.Add(item: geometry); changed = true; } else if (m_indirectMeshes[count] != geometry) { m_indirectMeshes[count] = geometry; changed = true; }
            count++;
        }
        if (count < m_indirectMeshes.Count) { m_indirectMeshes.RemoveRange(count, (m_indirectMeshes.Count - count)); changed = true; }
        if (changed) { m_indirectMeshRevision++; }
    }

    private readonly record struct IndirectMeshGeometry(SdfMesh Mesh, Matrix4x4 Pose, object Identity, bool FieldBacked,
        SdfMeshLod? Lod, SdfMeshImpostor? Impostor);
}
