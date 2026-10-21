using System.Collections.ObjectModel;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    private readonly ReadOnlyCollection<SdfMaterial> m_materialsView;

    /// <summary>Gets the immutable material values packed into this program, in material-identifier order.
    /// Nested reveal and paint lists are snapshotted too; a later caller edit cannot change CPU reference sources.</summary>
    public IReadOnlyList<SdfMaterial> Materials => m_materialsView;

    private bool? m_hasGlass;

    /// <summary>Gets whether any drawn shape of the program carries a glass material (<see cref="SdfMaterial.IsGlass"/>):
    /// a view of such a program runs the glass passes (<c>SdfWorldPackage.WithGlass</c>), and a view of any other pays
    /// nothing for them. Measured once, on first read.</summary>
    public bool HasGlass => (m_hasGlass ??= MeasureGlass());

    /// <summary>Returns whether a material id names a glass material of the program's table; a screen sentinel or an
    /// id past the table is opaque.</summary>
    /// <param name="material">The material id.</param>
    /// <returns><see langword="true"/> for a glass material.</returns>
    public bool IsGlassMaterial(int material) => ((material >= 0) && (material < m_materialsView.Count) && m_materialsView[material].IsGlass);
    /// <summary>Returns whether every drawn shape of an instruction range carries a glass material, and at least one
    /// shape is drawn: the shapes bake into a glass group. A detail shape counts as drawn; a range with no shape is
    /// opaque.</summary>
    /// <param name="first">The range's first instruction.</param>
    /// <param name="end">The instruction past the range's last.</param>
    /// <returns><see langword="true"/> for a range of glass shapes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range lies outside the instruction stream.</exception>
    public bool IsGlassRange(int first, int end) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: first);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: end, other: m_instructions.Length);

        var shapes = 0;

        for (var index = first; (index < end); index++) {
            var instruction = m_instructions[index];

            if (instruction.Op != SdfOp.ShapeBlend) { continue; }
            if (!IsGlassMaterial(material: unchecked((int)instruction.Material))) { return false; }
            shapes++;
        }

        return (shapes > 0);
    }

    private bool MeasureGlass() {
        foreach (var instruction in m_instructions) {
            if ((instruction.Op == SdfOp.ShapeBlend) && IsGlassMaterial(material: unchecked((int)instruction.Material))) {
                return true;
            }
        }

        return false;
    }

    private static SdfMaterial[] SnapshotMaterials(IReadOnlyList<SdfMaterial> materials) {
        var snapshot = new SdfMaterial[materials.Count];

        for (var index = 0; (index < snapshot.Length); index++) {
            var entry = materials[index];

            if (entry.Weathering is { Under: { } under } weathering) {
                entry = entry with { Weathering = weathering with { Under = Array.AsReadOnly(array: under.ToArray()) } };
            }
            if (entry.Inset is { Paint: { Stops: { } stops } paint } inset) {
                entry = entry with { Inset = inset with { Paint = paint with { Stops = Array.AsReadOnly(array: stops.ToArray()) } } };
            }
            if (entry.Paints is { } paints) {
                entry = entry with { Paints = Array.AsReadOnly(array: paints.ToArray()) };
            }
            snapshot[index] = entry;
        }
        return snapshot;
    }
}
