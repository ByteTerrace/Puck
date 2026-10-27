using Puck.Physics.Fields;
namespace Puck.World.Server;
/// <summary>One named physical region and its independent authoritative lattice.</summary>
public sealed record WorldFieldRegion(string Name, FieldLattice Lattice, WorldLatticeScalar? TravelResistance);
/// <summary>A physical region image with explicit region and field identities.</summary>
public sealed record WorldFieldRegionCheckpoint(string Region, IReadOnlyList<string> FieldNames, FieldLattice.Checkpoint Cells);
public sealed partial class WorldPopulation {
    private ulong FieldRegionsRevision() {
        var version = 0UL;

        for (var index = 0; (index < m_fieldRegions.Count); index++) { version = unchecked((version + ((ulong)m_fieldRegions[index].Lattice.Revision))); }
        return version;
    }

    /// <summary>Finds a region by topology identity.</summary>
    public WorldFieldRegion? TryFieldRegion(string name) => m_fieldRegions.FirstOrDefault(predicate: region => string.Equals(a: region.Name, b: name, comparisonType: StringComparison.Ordinal));
    /// <summary>Resolves a field row to its region and local ordinal.</summary>
    public bool TryFieldForRow(string name, out WorldFieldRegion? region, out int field) {
        foreach (var candidate in m_fieldRegions) {
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
