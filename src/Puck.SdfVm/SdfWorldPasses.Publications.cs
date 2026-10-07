using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.SdfVm;

// Captured by the actual Views recording, then committed only after that recording submits. A later cache
// publication cannot relabel the source that this image used. The closure epoch qualifies direct-only views too.
internal readonly record struct SdfViewLightingSource(SdfIndirectLightingSnapshot? Source, uint Stamp, long Closure,
    GpuImagePublication Environment, GpuImagePublication Screens);

public sealed partial class SdfWorldPasses {
    /// <inheritdoc/>
    public bool HoldsOutput(string instance, GpuImagePublication publication) =>
        (m_entries.TryGetValue(key: instance, value: out var entry) && (entry.ImagePublication == publication) && HoldsScreenClosureImage(instance: instance));
    /// <inheritdoc/>
    public void OutputPublished(string instance, GpuImagePublication publication) {
        var entry = Refresh(instance: instance);

        if ((entry.View is not { LightView: false }) || !publication.IsKnown ||
            (publication == entry.ImagePublication)) { return; }
        var source = entry.SubmittedLighting;

        if (!ReferenceEquals(objA: publication.Owner, objB: entry.ImagePublication.Owner) ||
            (source != entry.ImageLighting) || (publication.Sequence <= entry.ImagePublication.Sequence)) {
            entry.FirstImageSequence = publication.Sequence;
        }
        entry.ImagePublication = publication;
        entry.ImageLighting = source;
        entry.ImageLightingFence = entry.SubmittedLightingFence;
    }

    internal void SubmittedLighting(string instance, SdfViewLightingSource source, IGpuSubmissionFence fence) {
        var entry = Refresh(instance: instance);

        entry.SubmittedLighting = source;
        entry.SubmittedLightingFence = fence;
    }
    // Consecutive image publications with the same recorded source need only their inclusive range. This stays
    // bounded while a slow sibling is completing; no per-frame publication dictionary grows behind a parked view.
    internal bool ImageHasLighting(string instance, GpuImagePublication publication, SdfViewLightingSource source) =>
        (m_entries.TryGetValue(key: instance, value: out var entry) && (source == entry.ImageLighting) && publication.IsKnown &&
        ReferenceEquals(objA: publication.Owner, objB: entry.ImagePublication.Owner) &&
        (publication.Sequence >= entry.FirstImageSequence) && (publication.Sequence <= entry.ImagePublication.Sequence));

    private sealed partial class Entry {
        public SdfViewLightingSource SubmittedLighting;
        public IGpuSubmissionFence? SubmittedLightingFence;
        public SdfViewLightingSource ImageLighting;
        public IGpuSubmissionFence? ImageLightingFence;
        public GpuImagePublication ImagePublication;
        public long FirstImageSequence;
    }
}
