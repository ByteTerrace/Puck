using Puck.Physics.Fields;
namespace Puck.World.Server;
/// <summary>One named physical region and its independent authoritative lattice.</summary>
public sealed record WorldFieldRegion(string Name, FieldLattice Lattice, WorldLatticeScalar? TravelResistance);
/// <summary>A physical region image with explicit region and field identities.</summary>
public sealed record WorldFieldRegionCheckpoint(string Region, IReadOnlyList<string> FieldNames, FieldLattice.Checkpoint Cells);
public sealed partial class WorldPopulation {
    // Each region's BodyReadRevision as the sleepers last observed it (RecordSleepFieldObservation), so a change names
    // the regions it came from. Derived: a restore records the restored regions' stamps beside the sleepers' terms.
    private ulong[] m_sleepRegionRevisions = [];

    // What a body reads of the regions: every medium cell (SampleMediumSurfaces floats a body against it), and every
    // solid cell when the contact provider unions the lattices' solid columns, which only the field provider does
    // (WorldSolidField composes a FieldLatticeSolid over each region). Folds into ContactFieldVersion.
    private bool ContactReadsLatticeSolids => (m_baseContactField is WorldSolidField);
    private ulong FieldRegionsRevision() {
        var solids = ContactReadsLatticeSolids;
        var version = 0UL;

        for (var index = 0; (index < m_fieldRegions.Count); index++) { version = unchecked((version + m_fieldRegions[index].Lattice.BodyReadRevision(solids: solids))); }
        return version;
    }
    // Grows the box over the read bounds of every region whose body-read cells changed since the sleepers last observed
    // them: no body outside a region's read bounds reads any of its cells.
    private void FoldFieldRegionChanges(ref WorldColliderBox box) {
        var solids = ContactReadsLatticeSolids;

        for (var index = 0; (index < m_fieldRegions.Count); index++) {
            var lattice = m_fieldRegions[index].Lattice;

            if ((index < m_sleepRegionRevisions.Length) && (lattice.BodyReadRevision(solids: solids) == m_sleepRegionRevisions[index])) { continue; }
            box.Fold(minimum: lattice.ReadMinimum, maximum: lattice.ReadMaximum);
        }
    }
    private void RecordFieldRegionRevisions() {
        if (m_sleepRegionRevisions.Length != m_fieldRegions.Count) { m_sleepRegionRevisions = new ulong[m_fieldRegions.Count]; }
        var solids = ContactReadsLatticeSolids;

        for (var index = 0; (index < m_fieldRegions.Count); index++) { m_sleepRegionRevisions[index] = m_fieldRegions[index].Lattice.BodyReadRevision(solids: solids); }
    }

    /// <summary>Finds a region by topology identity.</summary>
    public WorldFieldRegion? TryFieldRegion(string name) {
        for (var index = 0; (index < m_fieldRegions.Count); index++) {
            var region = m_fieldRegions[index];

            if (string.Equals(a: region.Name, b: name, comparisonType: StringComparison.Ordinal)) { return region; }
        }
        return null;
    }
    /// <summary>Resolves a field row to its region and local ordinal.</summary>
    public bool TryFieldForRow(string name, out WorldFieldRegion? region, out int field) {
        for (var index = 0; (index < m_fieldRegions.Count); index++) {
            var candidate = m_fieldRegions[index];

            if (candidate.Lattice.TryFieldIndex(field: out field, name: name)) { region = candidate; return true; }
        }
        region = null; field = -1; return false;
    }
    /// <summary>Captures independent regions and identities.</summary>
    public IReadOnlyList<WorldFieldRegionCheckpoint> CaptureFieldRegions() => m_fieldRegions.Select(selector: region =>
        new WorldFieldRegionCheckpoint(region.Name, region.Lattice.Input.Fields.Select(selector: field => field.Name).ToArray(), region.Lattice.Capture())).ToArray();
    /// <summary>Validates all regions before live state changes.</summary>
    public void ValidateFieldRegions(IReadOnlyList<WorldFieldRegionCheckpoint>? checkpoints) {
        if ((checkpoints?.Count ?? 0) != m_fieldRegions.Count) { throw new InvalidOperationException(message: "The checkpoint's physical regions do not match its world definition."); }
        for (var index = 0; (index < m_fieldRegions.Count); index++) {
            var region = m_fieldRegions[index]; var image = checkpoints![index];

            if ((region.Name != image.Region) || !region.Lattice.Input.Fields.Select(selector: field => field.Name).SequenceEqual(image.FieldNames, StringComparer.Ordinal)) {
                throw new InvalidOperationException(message: $"The checkpoint's identities do not match region '{region.Name}'.");
            }
            region.Lattice.ValidateCheckpoint(checkpoint: image.Cells);
        }
    }
    /// <summary>Restores independently captured regions.</summary>
    public void RestoreFieldRegions(IReadOnlyList<WorldFieldRegionCheckpoint>? checkpoints) {
        ValidateFieldRegions(checkpoints: checkpoints);
        for (var index = 0; (index < m_fieldRegions.Count); index++) { m_fieldRegions[index].Lattice.Restore(checkpoint: checkpoints![index].Cells); }
    }
}
