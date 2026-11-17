namespace Puck.SdfVm.Tests;

// A test driver owns both lifetimes: frames share one dresser, and its draw claims end before its bake retires.
internal sealed class MeshBakeTestRun(SdfMeshPathBake bake, bool ownsLibrary = true) : IDisposable {
    // The tier a law asks of both the group it stores a mesh under and the bake that takes it: a bake with no view has no
    // demand to choose one, and the sealed geometry a mesh is stored under holds the tier it was planned at.
    public static Func<SdfMeshBakeGroup, int> PlannedTier { get; } = static _ => 0;

    public SdfMeshPathBake Bake { get; } = bake;
    public SdfMeshDresser Dresser { get; } = new(bake);

    private bool m_disposed;

    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        Dresser.Dispose();
        Bake.Retire();
        if (ownsLibrary) { Bake.Library.Dispose(); }
    }
}
