using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    private RenderGraphConvergence? ConvergenceOf(SdfWorldResidency residency) {
        var closure = ClosureOf(residency: residency);

        foreach (var entry in m_entries.Values) {
            if ((entry.Convergence is { Request.Completion.IsCompleted: false } convergence) &&
                (entry.Residency is { } participant) && (ReferenceEquals(objA: participant, objB: residency) ||
                    (closure?.Contains(residency: participant) == true))) { return convergence; }
        }
        return null;
    }

    /// <inheritdoc/>
    public bool TaintedOf(string instance) {
        var entry = Refresh(instance: instance);

        return ((entry.View is { } view) && (entry.HistoryTainted || SourceTainted(view.Residency.Tables, view.Residency.Frame)));
    }
    /// <inheritdoc/>
    public FrameRender CaptureReadinessOf(string instance) {
        var entry = Refresh(instance: instance);

        if ((entry.View is { } view) && (ClosureOf(residency: view.Residency) is { Complete: false })) {
            return FrameRender.Waiting(reason: $"the instance '{instance}' awaits its two complete screen-lighting rounds");
        }
        return FrameRender.Rendered;
    }

    // Whether a frame's lighting sources are tainted: a demanded sky environment or the screen emission it lights by.
    internal static bool SourceTainted(SdfWorldTables? tables, SdfFrame? frame) => ((tables is not null) && (frame is not null) &&
        ((tables.SkyEnvironmentDemand && (tables.SubmittedSkyEnvironment is { Tainted: true })) ||
            (!frame.DisableScreenLights && (tables.SubmittedScreenEmission is { Tainted: true }))));
    internal void SubmittedTaint(string instance, bool tainted, bool readsHistory) {
        var entry = Refresh(instance: instance);

        entry.HistoryTainted = (tainted || (readsHistory && entry.HistoryTainted));
    }

    private sealed partial class Entry {
        public bool HistoryTainted;
    }
}
