namespace Puck.World;

// The presentation's mesh-path bakes as the console reads them (world.wait bakes): settled since a mark once the engine is
// ready (WorldRenderProbe.IsReady), the world's presenter has captured a frame after the mark and holds the program that
// frame's content called for (SdfCompositionFrameSource.Building false), the world's residency (ViewHost) bakes that very
// program, and neither it nor any residency the binder renders besides it, a session view's, a routed scene's or an
// infinity layer's, owes a group of its program (SdfMeshPathBake.Pending). Without the frame and the program, a change
// delivered before the wait (a world.reload) reads settled while its program is still queued or building, since the
// residency's bake of the program before it is done. Read on the host pump.
public sealed partial class WorldScreenBinder : IWorldBakeReadiness {
    /// <inheritdoc/>
    public long Frames => (Presenter?.Composed.Frames ?? 0L);

    /// <inheritdoc/>
    public bool IsSettledSince(long mark) => (
        (m_renderProbe?.IsReady ?? false) &&
        (Presenter is { } presenter) &&
        (presenter.Composed.Frames > mark) &&
        !presenter.Composed.Building &&
        (ViewHost is { MeshPathBake: { Pending: false } bake }) &&
        ReferenceEquals(objA: bake.Program, objB: presenter.Composed.Program) &&
        !ViewsBake()
    );
    /// <inheritdoc/>
    public string? PendingReasonSince(long mark) {
        if (IsSettledSince(mark: mark)) {
            return null;
        }
        if (!(m_renderProbe?.IsReady ?? false) || (ViewHost is not { } world) || (Presenter is not { } presenter)) {
            return "the engine is not ready, so the world has drawn no program to bake";
        }
        if ((presenter.Composed.Frames <= mark) || presenter.Composed.Building) {
            return "the world's program is still being built from the content the wait armed on";
        }

        var names = new List<string>();

        if (world.MeshPathBake.Pending || !ReferenceEquals(objA: world.MeshPathBake.Program, objB: presenter.Composed.Program)) {
            names.Add(item: world.MeshPathBake.Describe());
        }
        foreach (var residency in m_routedResidencies.Values) {
            if (residency.MeshPathBake.Pending) { names.Add(item: residency.MeshPathBake.Describe()); }
        }
        foreach (var view in m_viewResidencies.Values) {
            if (view.Residency.MeshPathBake.Pending) { names.Add(item: view.Residency.MeshPathBake.Describe()); }
        }
        foreach (var entry in InfinityEntries()) {
            if (entry.Residency is { MeshPathBake.Pending: true } residency) { names.Add(item: residency.MeshPathBake.Describe()); }
        }

        return $"still baking: {string.Join(separator: ", ", values: names)}";
    }

    // Collects every generation once, on the first frame the bakes settle after boot. Loading leaves gigabytes of young
    // objects that the first ephemeral collections in play would otherwise promote, pausing a frame 10-25 ms; collected
    // here, at the end of the load, they are old already and later young collections copy only the frames' own garbage.
    private void CollectOnceSettled() {
        if (m_collectedSettled || !IsSettledSince(mark: 0L)) {
            return;
        }

        m_collectedSettled = true;
        GC.Collect(generation: 2, mode: GCCollectionMode.Forced, blocking: true, compacting: false);
    }
    private bool m_collectedSettled;
    // Whether a residency besides the world's own still owes its bake; allocates nothing, so a hold polls it every drain.
    private bool ViewsBake() {
        foreach (var residency in m_routedResidencies.Values) {
            if (residency.MeshPathBake.Pending) { return true; }
        }
        foreach (var view in m_viewResidencies.Values) {
            if (view.Residency.MeshPathBake.Pending) { return true; }
        }
        foreach (var root in m_infinityRoots.Values) {
            foreach (var entry in root.Entries.Values) {
                if (entry.Residency is { MeshPathBake.Pending: true }) { return true; }
            }
        }

        return false;
    }
}
