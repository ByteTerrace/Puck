using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

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

        return ((entry.View is { LightView: false } view) &&
            (entry.HistoryTainted || SourceTainted(view.Residency.Tables, view.Residency.Frame, readsIndirect: HasIndirectReaders(entry: entry))));
    }
    /// <inheritdoc/>
    public FrameRender CaptureReadinessOf(string instance) {
        var entry = Refresh(instance: instance);

        if (entry.View is not { LightView: false } view) {
            return FrameRender.Rendered;
        }
        if (ClosureOf(residency: view.Residency) is { Complete: false } closure) {
            if (closure.Members.Any(predicate: member => ((member.Residency.IndirectTier != SdfIndirectTier.Off) && member.Residency.IndirectFrozen &&
                HasIndirectReaders(residency: member.Residency)))) {
                return FrameRender.Refused(reason: $"the instance '{instance}' cannot complete its screen-lighting closure while a participating residency is frozen");
            }
            return FrameRender.Waiting(reason: $"the instance '{instance}' awaits its two complete screen-lighting rounds");
        }
        if ((view.Residency.IndirectTier == SdfIndirectTier.Off) || !HasIndirectReaders(instance: instance)) { return FrameRender.Rendered; }
        if (view.Residency.IndirectFrozen) {
            return FrameRender.Refused(reason: $"the instance '{instance}' cannot complete a cold capture solve while indirect updates are frozen");
        }
        if (!view.Residency.IsIndirectReady && (view.Residency.Tables?.Indirect is { } solving) &&
            (solving.CannotFinishReason(since: entry.ConvergenceInvalidations) is { } unfinishable)) {
            return FrameRender.Refused(reason: $"the instance '{instance}' cannot complete its capture's indirect solve: {unfinishable}");
        }
        // A converging capture's fenced receivers must belong to the sample the next render takes. Completion of the
        // preceding sample says nothing about a new sample's certificates, which its first render proves afresh.
        if ((entry.Convergence is { IsActive: true } convergence) && ((entry.ReceiverSubmittedSurface is not { } submitted) ||
            (submitted.Sample.Jitter != entry.Temporal.ConvergingJitter(counted: convergence.Samples)))) {
            return FrameRender.Waiting(reason: $"the instance '{instance}' awaits the receiver certificates of its converging sample {convergence.Samples}");
        }
        return ((view.Residency.IsIndirectReady && (view.Residency.Tables?.Indirect is { } cache) && ReceiversComplete(cache: cache, entry: entry) &&
            (cache.PublishedLightingSource is { Tainted: false }))
            ? FrameRender.Rendered
            : FrameRender.Waiting(reason: $"the instance '{instance}' awaits its capture source's complete fenced indirect solve and receiver certificates"));
    }

    internal static bool SourceTainted(SdfWorldTables? tables, SdfFrame? frame) => SourceTainted(tables, frame, readsIndirect: true);
    internal void SubmittedTaint(string instance, bool tainted, bool readsHistory) {
        var entry = Refresh(instance: instance);

        entry.HistoryTainted = (tainted || (readsHistory && entry.HistoryTainted));
    }

    private sealed partial class Entry {
        public bool HistoryTainted;
    }
}
