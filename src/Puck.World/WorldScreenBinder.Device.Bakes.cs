namespace Puck.World;

// The presentation's mesh-path bakes as the console reads them (world.wait bakes): settled once the engine is ready
// (WorldRenderProbe.IsReady, so the world's residency has drawn the program it bakes) and neither the world's residency
// (ViewHost) nor any residency the binder renders besides it, a session view's, a routed scene's or an infinity
// layer's, owes a group of its program (SdfMeshPathBake.Pending). Read on the host pump.
public sealed partial class WorldScreenBinder : IWorldBakeReadiness {
    /// <inheritdoc/>
    public bool IsSettled => (
        (m_renderProbe?.IsReady ?? false) &&
        (ViewHost is { MeshPathBake.Pending: false }) &&
        !ViewsBake()
    );
    /// <inheritdoc/>
    public string? PendingReason {
        get {
            if (IsSettled) {
                return null;
            }
            if (!(m_renderProbe?.IsReady ?? false) || (ViewHost is not { } world)) {
                return "the engine is not ready, so the world has drawn no program to bake";
            }

            var names = new List<string>();

            if (world.MeshPathBake.Pending) {
                names.Add(item: world.Name);
            }
            foreach (var residency in m_routedResidencies.Values) {
                if (residency.MeshPathBake.Pending) { names.Add(item: residency.Name); }
            }
            foreach (var view in m_viewResidencies.Values) {
                if (view.Residency.MeshPathBake.Pending) { names.Add(item: view.Residency.Name); }
            }
            foreach (var entry in InfinityEntries()) {
                if (entry.Residency is { MeshPathBake.Pending: true } residency) { names.Add(item: residency.Name); }
            }

            return $"still baking: {string.Join(separator: ", ", values: names)}";
        }
    }

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
