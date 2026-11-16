using Puck.Shaders;

namespace Puck.SdfVm;

// A hit a pass reads (a draw and a triangle in a mesh or glass target's texel, or in a visibility record) resolves through
// the draw records the tables hold when the reading pass records, so the target must have been written under those same
// records. Every record write moves the tables' mesh revision (SdfWorldTables.MeshRevision), which the geometry signature
// folds, so the writing pass renders again before any reader; this counts the reads that argument does not cover. Each
// pass that writes hits notes, per instance, the tables and the revision it recorded under, or the current revision when
// it skips over a target that holds a clear; each pass that resolves them counts a stale target
// (SdfMeshHistory.TargetsStale) when the note is not the tables' current revision. The mesh hit door
// (sdfMeshResolveHit) bounds such a read whatever its cause, but cannot tell an occupant that is in bounds from the one
// the target named; this count can.
public sealed partial class SdfWorldPasses {
    // The hit-holding outputs of a view's passes: the mesh target, the visibility records the surface pass writes from
    // it, and the glass target.
    private enum HitTarget : byte {
        Mesh = 0,
        Visibility = 1,
        Glass = 2,
    }

    /// <summary>Notes and checks the hit targets a pass of an instance writes and reads as it records: the mesh and glass
    /// raster passes note their targets; the surface pass checks the mesh target and notes its visibility records; the
    /// views and resolve passes check the visibility records; the glass shading pass checks the glass target. Every other
    /// part does nothing.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="part">The recording pass (<see cref="SdfWorldPackage.Parts"/>, or <see cref="SdfWorldPackage.Resolve"/>).</param>
    /// <param name="tables">The tables the pass records against.</param>
    internal void RecordHits(string instance, string part, SdfWorldTables tables) {
        var entry = Refresh(instance: instance);

        switch (part) {
            case SdfWorldPackage.Parts.Mesh:
                entry.Note(target: HitTarget.Mesh, tables: tables);
                break;
            case SdfWorldPackage.Parts.Glass:
                entry.Note(target: HitTarget.Glass, tables: tables);
                break;
            case SdfWorldPackage.Parts.Surface:
                entry.Check(target: HitTarget.Mesh, tables: tables);
                entry.Note(target: HitTarget.Visibility, tables: tables);
                break;
            case SdfWorldPackage.Parts.Views:
            case SdfWorldPackage.Resolve:
                entry.Check(target: HitTarget.Visibility, tables: tables);
                break;
            case SdfWorldPackage.Parts.GlassShade:
                entry.Check(target: HitTarget.Glass, tables: tables);
                break;
        }
    }
    /// <summary>Notes that an instance's mesh pass skipped over a target that holds the clear, which no draw record reads
    /// a hit from, so it stands for the tables' current revision.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="tables">The tables the pass would have recorded against.</param>
    internal void SkippedClearMeshTarget(string instance, SdfWorldTables tables) =>
        Refresh(instance: instance).Note(target: HitTarget.Mesh, tables: tables);

    private sealed partial class Entry {
        private readonly (SdfWorldTables? Tables, long Revision)[] m_hitTargets = new (SdfWorldTables?, long)[3];

        public void Note(HitTarget target, SdfWorldTables tables) =>
            m_hitTargets[(int)target] = (tables, tables.MeshRevision);
        public void Check(HitTarget target, SdfWorldTables tables) {
            var (noted, revision) = m_hitTargets[(int)target];

            if (!ReferenceEquals(objA: noted, objB: tables) || (revision != tables.MeshRevision)) {
                SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.TargetsStale);
            }
        }
    }
}
