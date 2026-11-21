using Puck.Shaders;

namespace Puck.SdfVm;

// A hit a pass reads (a draw and a triangle in a mesh or glass target's texel, or in a visibility record) resolves through
// the draw records the tables hold when the reading pass records, so the target must have been written under those same
// records for every draw it can name. Every write of a record the writing pass can show moves the revision of that pass's
// motion scope (SdfMotionScope), which its geometry signature folds, so the writing pass renders again before any reader;
// a record changing where the pass draws nothing leaves the target naming no row it changed. This counts the reads that
// argument does not cover. Each pass that writes hits notes, per instance, the tables and the scope revision it recorded
// under, or the current one when it skips over a target that holds a clear; each pass that resolves them counts a stale
// target (SdfMeshHistory.TargetsStale) when the note is not the scope's current revision. The mesh hit door
// (sdfMeshResolveHit) bounds such a read whatever its cause, but cannot tell an occupant that is in bounds from the one
// the target named; this count can.
public sealed partial class SdfWorldPasses {
    internal SdfMeshRowStamp MeshRowsOf(string instance) => Refresh(instance: instance).MeshRows;
    internal SdfMeshRowStamp NoteMeshRows(string instance, SdfMeshRowStamp stamp) {
        var entry = Refresh(instance: instance);
        var previous = entry.MeshRows;

        entry.MeshRows = stamp;
        return previous;
    }

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
                entry.Note(revision: HitRevisionOf(entry: entry, instance: instance, tables: tables, target: HitTarget.Mesh), tables: tables, target: HitTarget.Mesh);
                break;
            case SdfWorldPackage.Parts.Glass:
                entry.Note(revision: HitRevisionOf(entry: entry, instance: instance, tables: tables, target: HitTarget.Glass), tables: tables, target: HitTarget.Glass);
                break;
            case SdfWorldPackage.Parts.Surface:
                entry.Check(revision: HitRevisionOf(entry: entry, instance: instance, tables: tables, target: HitTarget.Mesh), tables: tables, target: HitTarget.Mesh);
                entry.Note(revision: HitRevisionOf(entry: entry, instance: instance, tables: tables, target: HitTarget.Visibility), tables: tables, target: HitTarget.Visibility);
                break;
            case SdfWorldPackage.Parts.ViewsProbes:
            case SdfWorldPackage.Parts.Views:
            case SdfWorldPackage.Resolve:
                entry.Check(revision: HitRevisionOf(entry: entry, instance: instance, tables: tables, target: HitTarget.Visibility), tables: tables, target: HitTarget.Visibility);
                break;
            case SdfWorldPackage.Parts.GlassShade:
                entry.Check(revision: HitRevisionOf(entry: entry, instance: instance, tables: tables, target: HitTarget.Glass), tables: tables, target: HitTarget.Glass);
                break;
        }
    }
    // The revision a hit target stands for: its writer's scope's, the glass pass's for the glass target and the mesh
    // pass's for the mesh target and the visibility records written from it.
    private long HitRevisionOf(Entry entry, string instance, HitTarget target, SdfWorldTables tables) => ((entry.View is { } view)
        ? MotionOf(entry: entry, instance: instance, part: ((target == HitTarget.Glass) ? SdfWorldPackage.Parts.Glass : SdfWorldPackage.Parts.Mesh), tables: tables, view: view)
        : 0L);
    /// <summary>Notes that an instance's mesh pass skipped over a target that holds the clear, which no draw record reads
    /// a hit from, so it stands for the tables' current revision.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="tables">The tables the pass would have recorded against.</param>
    internal void SkippedClearMeshTarget(string instance, SdfWorldTables tables) {
        var entry = Refresh(instance: instance);

        entry.Note(revision: HitRevisionOf(entry: entry, instance: instance, tables: tables, target: HitTarget.Mesh), tables: tables, target: HitTarget.Mesh);
    }

    private sealed partial class Entry {
        public SdfMeshRowStamp MeshRows;

        private readonly (SdfWorldTables? Tables, long Revision)[] m_hitTargets = new (SdfWorldTables?, long)[3];

        public void Note(HitTarget target, SdfWorldTables tables, long revision) =>
            m_hitTargets[((int)target)] = (tables, revision);
        public void Check(HitTarget target, SdfWorldTables tables, long revision) {
            var (noted, recorded) = m_hitTargets[((int)target)];

            if (!ReferenceEquals(objA: noted, objB: tables) || (recorded != revision)) {
                SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.TargetsStale);
            }
        }
    }
}
