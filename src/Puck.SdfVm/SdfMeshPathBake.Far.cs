namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // Names each mesh a listed group shows, or keeps as the floor of its shape, by the group's shape and the material
    // ids its triangles carry: what identifies the mesh to a far product whatever tier it was baked at
    // (SdfFarProduct.Take). Takes the gate; on the frame's thread.
    internal void DescribeShown(Dictionary<SdfMesh, SdfFarSource> into) {
        using (EnterGate(site: SdfMeshBakeReport.Gate.Observe)) {
            foreach (var group in m_groups) {
                if (group.Retired) { continue; }

                var source = new SdfFarSource(Materials: group.MaterialOrder, Shape: group.Shape);

                if (group.Shown is { } shown) { into[shown] = source; }
                if (group.CurrentFloor is { } floor) { into[floor] = source; }
            }
        }
    }
}
