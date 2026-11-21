using Puck.SdfVm;

namespace Puck.World;

// The presentation's mesh-path bakes as the console reads them (world.wait bakes): settled since a mark once the engine is
// ready (WorldRenderProbe.IsReady), the world's presenter has captured SdfMeshTier.SettleFrames frames after the mark and
// holds the program the latest one's content called for (SdfCompositionFrameSource.Building false), the world's
// residency (ViewHost) bakes that very program with its meshes at the detail its demand asks
// (SdfMeshPathBake.DetailSettled), and neither it nor any residency the binder renders besides it, a session view's, a
// routed scene's or an infinity layer's, owes a group of its program while a view draws it (SdfMeshPathBake.Pending,
// Driven). Without the frames and the
// program, a change delivered before the wait (a world.reload, a camera moved to a stop) reads settled while its program
// is still queued or building, or while the bake has yet to measure its groups against the moved demand, since the
// residency's bake of what came before it is done. The detail is the world's own residency's: the others draw at their
// own cadence, so one that stops drawing would hold a wait on frames that never come. Read on the host pump.
public sealed partial class WorldScreenBinder : IWorldBakeReadiness {
    /// <inheritdoc/>
    public long Frames => (Presenter?.Composed.Frames ?? 0L);

    /// <inheritdoc/>
    public bool IsSettledSince(long mark) => (
        (m_renderProbe?.IsReady ?? false) &&
        (Presenter is { } presenter) &&
        ((presenter.Composed.Frames - mark) >= SdfMeshTier.SettleFrames) &&
        !presenter.Composed.Building &&
        (ViewHost is { MeshPathBake: { Pending: false, DetailSettled: true } bake }) &&
        ReferenceEquals(objA: bake.Program, objB: presenter.Composed.Program) &&
        !ViewsBake()
    );
    /// <inheritdoc/>
    public string? PendingReasonSince(long mark) {
        if (IsSettledSince(mark: mark)) {
            return null;
        }
        if ((ViewHost is not { } world) || (Presenter is not { } presenter)) {
            return "the engine is not ready, so the world has drawn no program to bake";
        }
        // The bake starts on its own kernels while the frame's pipelines still build (SdfWorldPipelines.PollRequired).
        if (!(m_renderProbe?.IsReady ?? false)) {
            var notReady = (m_renderProbe?.NotReadyReason ?? "the renderer has not been composed");

            return ((world.MeshPathBake.Program is null)
                ? $"the engine is not ready ({notReady}), so the world has drawn no program to bake"
                : $"the engine is not ready ({notReady}), while the world's bake runs: {world.MeshPathBake.Describe()}");
        }
        if ((presenter.Composed.Frames <= mark) || presenter.Composed.Building) {
            return "the world's program is still being built from the content the wait armed on";
        }
        if ((presenter.Composed.Frames - mark) < SdfMeshTier.SettleFrames) {
            return "the world's bake is still measuring its groups against the demand the wait armed on";
        }

        var names = new List<string>();

        if (world.MeshPathBake.Pending || !ReferenceEquals(objA: world.MeshPathBake.Program, objB: presenter.Composed.Program)) {
            names.Add(item: world.MeshPathBake.Describe());
        }
        foreach (var residency in m_routedResidencies.Values) {
            if (Bakes(bake: residency.MeshPathBake)) { names.Add(item: residency.MeshPathBake.Describe()); }
        }
        foreach (var view in m_viewResidencies.Values) {
            if (Bakes(bake: view.Residency.MeshPathBake)) { names.Add(item: view.Residency.MeshPathBake.Describe()); }
        }
        foreach (var entry in InfinityEntries()) {
            if ((entry.Residency is { } residency) && Bakes(bake: residency.MeshPathBake)) { names.Add(item: residency.MeshPathBake.Describe()); }
        }

        if ((names.Count == 0) && !world.MeshPathBake.DetailSettled) {
            return $"the world's meshes are still moving to the detail their demand asks: {world.MeshPathBake.Describe()}";
        }

        return $"still baking: {string.Join(separator: ", ", values: names)}";
    }

    /// <inheritdoc/>
    public bool IsResidentSince(long mark) => IsSettledSince(mark) && PendingMeshResidency is null;
    /// <inheritdoc/>
    public string? PendingResidencyReasonSince(long mark) => PendingReasonSince(mark) ?? PendingMeshResidency;
    private string? PendingMeshResidency => (ViewHost is not { } host)
        ? "the world has no residency"
        : (host.MeshResidencyPending is { } pending)
        ? $"the world's residency {pending}"
        : m_renderProbe is { Root.Runtime: { } runtime, Views: { } views }
            ? views.PendingMeshesIn(runtime)
            : "the render graph is not ready";
    // Whether a residency besides the world's own still owes its bake; allocates nothing, so a hold polls it every drain.
    private bool ViewsBake() {
        foreach (var residency in m_routedResidencies.Values) {
            if (Bakes(bake: residency.MeshPathBake)) { return true; }
        }
        foreach (var view in m_viewResidencies.Values) {
            if (Bakes(bake: view.Residency.MeshPathBake)) { return true; }
        }
        foreach (var root in m_infinityRoots.Values) {
            foreach (var entry in root.Entries.Values) {
                if ((entry.Residency is { } residency) && Bakes(bake: residency.MeshPathBake)) { return true; }
            }
        }

        return false;
    }
    // Whether a residency besides the world's own holds a wait on the bakes: its bake owes a group, or is between the
    // rungs of a ladder it is filling (SdfMeshPathBake.Filling), while a view draws it (SdfMeshPathBake.Driven). One no
    // view draws lands nothing, so a product of its left waiting when its last view turned away holds no wait.
    private static bool Bakes(SdfMeshPathBake bake) => ((bake.Pending || bake.Filling) && bake.Driven);
}
