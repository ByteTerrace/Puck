using Puck.Hosting;
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
    /// <remarks>Names what holds the content: the latest views render, by what it read (a lighting source or a read
    /// instance), or the temporal history that read it earlier and blends it into every later render, by what it read
    /// then; otherwise the lighting source the views would read now.</remarks>
    public string? TaintOf(string instance) {
        var entry = Refresh(instance: instance);

        return ((entry.View is not { } view)
            ? null
            : (entry.HistoryTainted
                ? (entry.TaintedNow
                    ? $"the latest views render, which read {entry.TaintCause}"
                    : $"the temporal history, which read {entry.TaintCause} and blends it into every later render")
                : SourceTaintOf(view.Residency.Tables, view.Residency.Frame)));
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
        entry.TaintedNow = false;
        entry.TaintCause = null;

        return ((int)SdfTemporalSamples.Period);
    }

    // Names a frame's tainted lighting source, a demanded sky environment or the screen emission it lights by, or null
    // when neither is.
    internal static string? SourceTaintOf(SdfWorldTables? tables, SdfFrame? frame) => (((tables is null) || (frame is null))
        ? null
        : ((tables.SkyEnvironmentDemand && (tables.SubmittedSkyEnvironment is { Tainted: true }))
            ? "the sky environment, projected from a panorama screen's external image"
            : ((!frame.DisableScreenLights && (tables.SubmittedScreenEmission is { Tainted: true }))
                ? "the screen emission the screen lights read, reduced from a screen's external image"
                : null)));
    // Names the first read a render bound with external content the capture gate did not fill, or null when none.
    internal static string? ReadTaintOf(RenderGraphExternalReads? reads) {
        for (var index = 0; (index < (reads?.Count ?? 0)); index++) {
            if (reads![index].Tainted) {
                // Spelled once for each producer, since a view reading a live external image is tainted every frame.
                return ReadTaints.GetValue(key: reads[index].Producer, createValueCallback: static producer => $"the unfilled external image of '{producer}'");
            }
        }

        return null;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, string> ReadTaints = new();
    // Records a views render's taint: what it read, or null when it read nothing external, and whether it read history.
    internal void SubmittedTaint(string instance, string? cause, bool readsHistory) {
        var entry = Refresh(instance: instance);

        entry.TaintedNow = (cause is not null);
        entry.HistoryTainted = (entry.TaintedNow || (readsHistory && entry.HistoryTainted));
        entry.TaintCause = (cause ?? (entry.HistoryTainted ? entry.TaintCause : null));
    }

    private sealed partial class Entry {
        public bool HistoryTainted;
        // Whether the latest views render itself read external content, and what the history's taint came from.
        public bool TaintedNow;
        public string? TaintCause;
    }
}
