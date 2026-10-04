namespace Puck.SdfVm;

// The existing view readback fence completes this exact shared publication, independently of that view's receiver
// certificate scope. Lighting edits must neither relabel a completed fence nor invalidate unchanged receiver proofs.
internal readonly record struct SdfIndirectLightingCompletion(SdfIndirectHistory History, uint Stamp, ulong Source);

public sealed partial class SdfIndirectCache {
    private SdfIndirectLightingCompletion m_completedLighting;

    internal SdfIndirectLightingCompletion LightingCompletion => new(History, PublishedStamp,
        PublishedLightingSource?.Sequence ?? 0UL);

    internal void CompleteLightingReadback(SdfIndirectLightingCompletion completion) {
        if (completion.Stamp != 0u && completion.Source != 0UL && completion == LightingCompletion) {
            m_completedLighting = completion;
        }
    }

    internal bool IsReadyFor(SdfFrame frame) => IsComplete && LightingComplete && PublishedStamp != 0u &&
        PublishedLightingSource is not null && ReferenceEquals(PublishedLightingSource, LightingSource) &&
        Lighting is { } lighting && lighting.Matches(frame) && m_completedLighting == LightingCompletion;
}
