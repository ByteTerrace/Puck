using Puck.Testing;

namespace Puck.SdfVm.Tests;

// A test driver owns both lifetimes: frames share one dresser, and its draw claims end before its bake retires.
internal sealed class MeshBakeTestRun(SdfMeshPathBake bake, bool ownsLibrary = true) : IDisposable {
    // The tier a law asks of both the group it stores a mesh under and the bake that takes it: a bake with no view has no
    // demand to choose one, and the sealed geometry a mesh is stored under holds the tier it was planned at.
    public static Func<SdfMeshBakeGroup, int> PlannedTier { get; } = static _ => 0;

    public SdfMeshPathBake Bake { get; } = bake;
    public SdfMeshDresser Dresser { get; } = new(bake);

    private bool m_disposed;

    // Dresses the frame and takes once no group holds a stage on the pool, returning the first batch taken. A take bounds
    // a grid lattice on the pool before it samples it (SdfMeshPathBake.Bounds.cs) and takes nothing for a group it hands
    // there, so the first take of a fresh bake queues the bounds and takes nothing; a law that reads one take's choice
    // among groups (its order, its lattices) reads the take made once every group offers its lattice.
    public SdfMeshBakeBatch TakeOffered(SdfFrame frame) {
        SdfMeshBakeBatch? taken = null;

        TestLiveness.Until(reason: Bake.Describe, step: () => {
            var seen = SdfMeshPathBake.Progress;

            _ = Dresser.Dress(frame: frame);
            if (Bake.WorkingGroups > 0) {
                _ = SdfMeshPathBake.WaitForProgress(since: seen, timeout: TimeSpan.FromMilliseconds(milliseconds: 250));
                return false;
            }
            return Bake.TryTake(batch: out taken, field: Bake.Program);
        });

        return taken!;
    }

    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        Dresser.Dispose();
        Bake.Retire();
        if (ownsLibrary) { Bake.Library.Dispose(); }
    }
}
