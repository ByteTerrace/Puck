namespace Puck.SdfVm;

// A completed zero belongs to this exact allocation, transport scope and primary write, never a later camera sample.
internal readonly record struct SdfIndirectReceiverScope(long Allocation, uint Certificate, long Surface);

public sealed partial class SdfWorldPasses {
    internal void ReceiverSurfaceWritten(string instance) {
        var entry = Refresh(instance);
        entry.ReceiverSurface = checked(entry.ReceiverSurface + 1);
    }

    internal SdfIndirectReceiverScope PrepareReceivers(string instance, SdfIndirectCache cache) {
        var entry = Refresh(instance);
        var scope = new SdfIndirectReceiverScope(cache.History.Allocation, cache.CertificateRevision, entry.ReceiverSurface);
        if (entry.ReceiverScope != scope) {
            entry.ReceiverScope = scope;
            entry.ReceiversPending = true;
        }
        if (entry.ReceiversPending) { cache.AdmitReceivers(); }
        return scope;
    }

    internal void CompletedReceivers(string instance, SdfIndirectReceiverScope scope, SdfIndirectLightingCompletion lighting, uint deferred) {
        if (!m_entries.TryGetValue(instance, out var entry)) { return; }
        entry.View?.Residency.Tables?.Indirect?.CompleteLightingReadback(lighting);
        if (entry.ReceiverScope == scope) { entry.ReceiversPending = deferred != 0u; }
    }

    private static bool ReceiversPending(Entry entry) => entry.ReceiversPending &&
        entry.View is { LightView: false, Residency.Tables.Indirect: { Frozen: false } };

    private sealed partial class Entry {
        public long ReceiverSurface;
        public SdfIndirectReceiverScope ReceiverScope;
        public bool ReceiversPending;
    }
}
