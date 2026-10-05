using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    /// <inheritdoc/>
    public bool TaintedOf(string instance) {
        var entry = Refresh(instance: instance);

        return ((entry.View is { LightView: false } view) &&
            (entry.HistoryTainted || SourceTainted(view.Residency.Tables, view.Residency.Frame)));
    }
    /// <inheritdoc/>
    public FrameRender CaptureReadinessOf(string instance) {
        var entry = Refresh(instance: instance);

        if (entry.View is not { LightView: false } view) {
            return FrameRender.Rendered;
        }
        if (ClosureOf(residency: view.Residency) is { Complete: false } closure) {
            if (closure.Members.Any(predicate: member => ((member.Residency.IndirectTier != SdfIndirectTier.Off) && member.Residency.IndirectFrozen))) {
                return FrameRender.Refused(reason: $"the instance '{instance}' cannot complete its screen-lighting closure while a participating residency is frozen");
            }
            return FrameRender.Waiting(reason: $"the instance '{instance}' awaits its two complete screen-lighting rounds");
        }
        if (view.Residency.IndirectTier == SdfIndirectTier.Off) { return FrameRender.Rendered; }
        if (view.Residency.IndirectFrozen) {
            return FrameRender.Refused(reason: $"the instance '{instance}' cannot complete a cold capture solve while indirect updates are frozen");
        }
        return ((view.Residency.IsIndirectReady && (view.Residency.Tables?.Indirect is { } cache) && ReceiversComplete(cache: cache, entry: entry) &&
            (cache.PublishedLightingSource is { Tainted: false }))
            ? FrameRender.Rendered
            : FrameRender.Waiting(reason: $"the instance '{instance}' awaits its capture source's complete fenced indirect solve and receiver certificates"));
    }

    internal static bool SourceTainted(SdfWorldTables? tables, SdfFrame? frame) => ((tables is not null) && (frame is not null) &&
        ((tables.Indirect?.PublishedLightingSource is { Tainted: true }) ||
            (tables.SkyEnvironmentDemand && (tables.SubmittedSkyEnvironment is { Tainted: true })) ||
            (!frame.DisableScreenLights && (tables.SubmittedScreenEmission is { Tainted: true }))));
    internal void SubmittedTaint(string instance, bool tainted, bool readsHistory) {
        var entry = Refresh(instance: instance);

        entry.HistoryTainted = (tainted || (readsHistory && entry.HistoryTainted));
    }

    private sealed partial class Entry {
        public bool HistoryTainted;
    }
}
