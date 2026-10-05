using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// A completed zero belongs to this exact allocation, transport scope and submitted surface sample.
internal readonly record struct SdfIndirectReceiverScope(long Allocation, uint Certificate, long Surface);
internal readonly record struct SdfIndirectReceiverSurface(object Recorder, IGpuBuffer Buffer, long Binding,
    ulong Geometry, SdfReprojectionView Sample);

public sealed partial class SdfWorldPasses {
    internal bool PrepareReceiverSurface(string instance, object recorder, IGpuBuffer buffer, ulong geometry, SdfReprojectionView sample, bool stableGeometry) {
        var entry = Refresh(instance: instance);
        var surface = new SdfIndirectReceiverSurface(recorder, buffer, entry.Bindings, geometry, sample);

        entry.ReceiverRecordedSurface = (stableGeometry ? surface : null);
        var preserve = (stableGeometry && (entry.ReceiverSubmittedSurface is { } submitted) &&
            ReferenceEquals(objA: submitted.Recorder, objB: recorder) && ReferenceEquals(objA: submitted.Buffer, objB: buffer) &&
            (submitted.Binding == surface.Binding) && (submitted.Geometry == geometry) && (submitted.Sample == sample));

        if (!preserve) { entry.ReceiverSurface = checked((entry.ReceiverSurface + 1)); }
        return preserve;
    }
    internal void SubmittedReceiverSurface(string instance) {
        var entry = Refresh(instance: instance);

        entry.ReceiverSubmittedSurface = entry.ReceiverRecordedSurface;
    }
    internal SdfIndirectReceiverScope PrepareReceivers(string instance, SdfIndirectCache cache) {
        var entry = Refresh(instance: instance);
        var scope = new SdfIndirectReceiverScope(Allocation: cache.History.Allocation, Certificate: cache.CertificateRevision, Surface: entry.ReceiverSurface);

        if (entry.ReceiverScope != scope) {
            entry.ReceiverScope = scope;
            entry.ReceiversPending = true;
        }
        if (entry.ReceiversPending) { cache.AdmitReceivers(); }
        return scope;
    }
    internal void CompletedReceivers(string instance, SdfIndirectReceiverScope scope, SdfIndirectLightingCompletion lighting, uint deferred) {
        if (!m_entries.TryGetValue(key: instance, value: out var entry)) { return; }
        entry.View?.Residency.Tables?.Indirect?.CompleteLightingReadback(completion: lighting);
        if (entry.ReceiverScope == scope) {
            entry.ReceiversPending = (deferred != 0u);
            if (deferred == 0u) { entry.ReceiverCompletedScope = scope; }
        }
    }

    private static bool ReceiversComplete(Entry entry, SdfIndirectCache cache) =>
        ((entry.ReceiverSubmittedSurface is not null) &&
        (entry.ReceiverScope == new SdfIndirectReceiverScope(Allocation: cache.History.Allocation, Certificate: cache.CertificateRevision, Surface: entry.ReceiverSurface)) &&
        (entry.ReceiverCompletedScope == entry.ReceiverScope) && !entry.ReceiversPending);
    private static bool ReceiversPending(Entry entry) => (entry.ReceiversPending &&
        (entry.View is { LightView: false, Residency.Tables.Indirect: { Frozen: false } }));

    private sealed partial class Entry {
        public long ReceiverSurface;
        public SdfIndirectReceiverSurface? ReceiverRecordedSurface;
        public SdfIndirectReceiverSurface? ReceiverSubmittedSurface;
        public SdfIndirectReceiverScope ReceiverScope;
        public SdfIndirectReceiverScope ReceiverCompletedScope;
        public bool ReceiversPending;
    }
}
