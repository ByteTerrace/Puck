namespace Puck.SdfVm.Tests;

// A test driver owns both lifetimes: frames share one dresser, and its draw claims end before its bake retires.
internal sealed class MeshBakeTestRun(SdfMeshPathBake bake, bool ownsLibrary = true) : IDisposable {
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
