using System.Collections.ObjectModel;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    private readonly ReadOnlyCollection<SdfMaterial> m_materialsView;

    /// <summary>Gets the immutable material values packed into this program, in material-identifier order.
    /// Nested reveal and paint lists are snapshotted too; a later caller edit cannot change CPU reference sources.</summary>
    public IReadOnlyList<SdfMaterial> Materials => m_materialsView;

    private static SdfMaterial[] SnapshotMaterials(IReadOnlyList<SdfMaterial> materials) {
        var snapshot = new SdfMaterial[materials.Count];
        for (var index = 0; index < snapshot.Length; index++) {
            var entry = materials[index];
            if (entry.Weathering is { Under: { } under } weathering) {
                entry = entry with { Weathering = weathering with { Under = Array.AsReadOnly(under.ToArray()) } };
            }
            if (entry.Inset is { Paint: { Stops: { } stops } paint } inset) {
                entry = entry with { Inset = inset with { Paint = paint with { Stops = Array.AsReadOnly(stops.ToArray()) } } };
            }
            snapshot[index] = entry;
        }
        return snapshot;
    }
}
