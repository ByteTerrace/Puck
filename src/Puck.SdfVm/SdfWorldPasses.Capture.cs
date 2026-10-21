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
    /// <remarks>Names the temporal history that read external content, or the lighting source the views read
    /// (<see cref="SourceTaintOf"/>).</remarks>
    public string? TaintOf(string instance) {
        var entry = Refresh(instance: instance);

        return ((entry.View is not { } view)
            ? null
            : (entry.HistoryTainted ? "the temporal history, which read external content and blends it into every later render" : SourceTaintOf(view.Residency.Tables, view.Residency.Frame)));
    }

    /// <inheritdoc/>
    /// <remarks>Restarts the instance's temporal history (<see cref="SdfTemporalHistory.Reset"/>) when it read external
    /// content, which then needs one <see cref="SdfTemporalSamples.Period"/> of renders to converge again, and has a
    /// tainted lighting source (the sky environment, the screen emission) derived again on its next recording
    /// (<see cref="SdfWorldTables.ProjectTaintedSourcesAgain"/>), from the capture gate's fills, before this instance's
    /// views read it in the same frame.</remarks>
    public int DiscardTaintedState(string instance) {
        var entry = Refresh(instance: instance);

        _ = ((entry.View?.Residency.Tables is { } tables) && tables.ProjectTaintedSourcesAgain());

        if (!entry.HistoryTainted) {
            return 0;
        }

        entry.Temporal.Reset();
        entry.HistoryTainted = false;

        return ((int)SdfTemporalSamples.Period);
    }

    // Whether a frame's lighting sources are tainted: a demanded sky environment or the screen emission it lights by.
    internal static bool SourceTainted(SdfWorldTables? tables, SdfFrame? frame) => (SourceTaintOf(tables: tables, frame: frame) is not null);
    // Names the frame's tainted lighting source, or null when neither is.
    private static string? SourceTaintOf(SdfWorldTables? tables, SdfFrame? frame) => (((tables is null) || (frame is null))
        ? null
        : ((tables.SkyEnvironmentDemand && (tables.SubmittedSkyEnvironment is { Tainted: true }))
            ? "the sky environment, projected from a panorama screen's external image"
            : ((!frame.DisableScreenLights && (tables.SubmittedScreenEmission is { Tainted: true }))
                ? "the screen emission the screen lights read, reduced from a screen's external image"
                : null)));
    internal void SubmittedTaint(string instance, bool tainted, bool readsHistory) {
        var entry = Refresh(instance: instance);

        entry.HistoryTainted = (tainted || (readsHistory && entry.HistoryTainted));
    }

    private sealed partial class Entry {
        public bool HistoryTainted;
    }
}
