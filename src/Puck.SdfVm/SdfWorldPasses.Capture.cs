using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    private RenderGraphConvergence? ConvergenceOf(SdfWorldResidency residency) {
        foreach (var entry in m_entries.Values) {
            if ((entry.Convergence is { Request.Completion.IsCompleted: false } convergence) &&
                ReferenceEquals(objA: entry.Residency, objB: residency)) { return convergence; }
        }
        return null;
    }

    /// <inheritdoc/>
    public bool TaintedOf(string instance) {
        var entry = Refresh(instance: instance);

        return ((entry.View is { } view) && (entry.HistoryTainted || SourceTainted(view.Residency.Tables, view.Residency.Frame)));
    }

    /// <inheritdoc/>
    /// <remarks>Restarts the instance's temporal history (<see cref="SdfTemporalHistory.Reset"/>) when it read external
    /// content, which then needs one <see cref="SdfTemporalSamples.Period"/> of renders to converge again. Tainted lighting
    /// sources are not history: the capture gate's fill replaces them on the next submission.</remarks>
    public int DiscardTaintedHistory(string instance) {
        var entry = Refresh(instance: instance);

        if (!entry.HistoryTainted) {
            return 0;
        }

        entry.Temporal.Reset();
        entry.HistoryTainted = false;

        return ((int)SdfTemporalSamples.Period);
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
