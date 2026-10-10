using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// A completed zero belongs to this exact allocation, transport scope and submitted surface sample.
internal readonly record struct SdfIndirectReceiverScope(long Allocation, uint Certificate, long Surface);
// Converging and ConvergedSample name the converging capture and the sample index (RenderGraphConvergence.Samples) the
// surface's render took, or null and zero outside convergence: the exact sample a converging capture serves.
internal readonly record struct SdfIndirectReceiverSurface(object Recorder, IGpuBuffer Buffer, long Binding,
    ulong Geometry, SdfReprojectionView Sample, long Cut, double Scale, bool Temporal, RenderGraphConvergence? Converging, int ConvergedSample);

public sealed partial class SdfWorldPasses {
    internal static bool SourceTainted(SdfWorldTables? tables, SdfFrame? frame, bool readsIndirect) => ((tables is not null) && (frame is not null) &&
        ((readsIndirect && (tables.Indirect?.PublishedLightingSource is { Tainted: true })) ||
            (tables.SkyEnvironmentDemand && (tables.SubmittedSkyEnvironment is { Tainted: true })) ||
            (!frame.DisableScreenLights && (tables.SubmittedScreenEmission is { Tainted: true }))));

    /// <summary>Returns whether this view needs indirect work. Only a fenced zero for its unchanged submitted
    /// surface proves otherwise; unknown surfaces and diagnostic views keep demand.</summary>
    /// <param name="instance">The ordinary view instance.</param>
    /// <returns>Whether indirect producers remain demanded.</returns>
    public bool HasIndirectReaders(string instance) => HasIndirectReaders(entry: Refresh(instance: instance));

    private static bool HasIndirectReaders(Entry entry) {
        if ((entry.View is not { LightView: false } view) || (view.Residency.Tables is not { } tables) ||
            (view.Residency.Frame is not { Views.Count: > 0 } frame) || tables.LightGeometryMutable ||
            (tables.PassValues.DebugMode != 0) || (entry.ReaderCount != 0u) ||
            (entry.ReaderCompletedSurface != entry.ReceiverSurface) || (entry.ReceiverSubmittedSurface is not { } surface)) { return true; }
        var index = Math.Min(val1: view.View, val2: (frame.Views.Count - 1));
        var snapshot = frame.Views[index];

        return ((surface.Binding != entry.Bindings) || (surface.Cut != snapshot.CutRevision) ||
            (surface.Scale != entry.CurrentScale) || (surface.Temporal != entry.RequestsTemporal) ||
            (surface.Sample.Camera != snapshot.Camera) ||
            (entry.ReceiverDemandExtent != (surface.Sample.Width, surface.Sample.Height)) ||
            (surface.Geometry != tables.PassSignature(frame: frame, part: SdfWorldPackage.Parts.Primary, view: index)));
    }

    /// <summary>Returns whether any tracked ordinary view needs this residency's indirect producers. A residency
    /// without a tracked ordinary view remains demanded.</summary>
    /// <param name="residency">The shared producer's residency.</param>
    /// <returns>Whether at least one view has readers or lacks exact zero-reader evidence.</returns>
    public bool HasIndirectReaders(SdfWorldResidency residency) {
        var found = false;

        foreach (var (instance, entry) in m_entries) {
            if ((entry.View is not { LightView: false } view) || !ReferenceEquals(objA: view.Residency, objB: residency)) { continue; }
            found = true;
            if (HasIndirectReaders(instance: instance)) { return true; }
        }
        return !found;
    }

    // Returns whether the visibility storage repeats: the same recorder, buffer, binding and extent, so every pixel's
    // retained certificate still belongs to that pixel. A new camera sample or geometry keeps the certificates; the
    // receiver pass keeps each only while its certified launch still joins the pixel's new surface point. A changed
    // surface still opens a new receiver scope, whose census the capture and readiness waits judge.
    internal bool PrepareReceiverSurface(string instance, object recorder, IGpuBuffer buffer, ulong geometry, SdfReprojectionView sample, long cut, bool stableGeometry) {
        var entry = Refresh(instance: instance);
        var converging = ((entry.Convergence is { IsActive: true } active) ? active : null);
        var surface = new SdfIndirectReceiverSurface(recorder, buffer, entry.Bindings, geometry, sample, cut, entry.CurrentScale, entry.RequestsTemporal,
            converging, (converging?.Samples ?? 0));
        var previous = entry.ReceiverSubmittedSurface;

        entry.ReceiverRecordedSurface = (stableGeometry ? surface : null);
        var storage = (stableGeometry && (previous is { } stored) && ReferenceEquals(objA: stored.Recorder, objB: recorder) && ReferenceEquals(objA: stored.Buffer, objB: buffer) &&
            (stored.Binding == surface.Binding) && (stored.Scale == surface.Scale) &&
            ((stored.Sample.Width, stored.Sample.Height) == (sample.Width, sample.Height)));
        var preserve = (storage && (previous is { } submitted) && (submitted.Geometry == geometry) && (submitted.Sample == sample) &&
            (submitted.Cut == cut) && (submitted.Temporal == surface.Temporal));

        if (!preserve) { entry.ReceiverSurface = checked((entry.ReceiverSurface + 1)); }
        return storage;
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
        if (entry.ReceiversPending && HasIndirectReaders(entry: entry)) { cache.AdmitReceivers(); }
        return scope;
    }
    internal void CompletedReceivers(string instance, SdfIndirectReceiverScope scope, SdfIndirectLightingCompletion lighting, uint deferred, uint readers) {
        if (!m_entries.TryGetValue(key: instance, value: out var entry)) { return; }
        entry.View?.Residency.Tables?.Indirect?.CompleteLightingReadback(completion: lighting);
        // Reader demand belongs to the surface, independently of a solve's advancing publication or certificate.
        if ((scope.Surface == entry.ReceiverSurface) && (deferred == 0u) && (entry.ReceiverSubmittedSurface is not null)) {
            entry.ReaderCount = readers;
            entry.ReaderCompletedSurface = scope.Surface;
        }
        if (entry.ReceiverScope == scope) {
            entry.ReceiversPending = (deferred != 0u);
            if (deferred == 0u) { entry.ReceiverCompletedScope = scope; }
        }
    }

    private static bool ReceiversComplete(Entry entry, SdfIndirectCache cache) =>
        ((entry.ReceiverSubmittedSurface is not null) &&
        (entry.ReceiverScope == new SdfIndirectReceiverScope(Allocation: cache.History.Allocation, Certificate: cache.CertificateRevision, Surface: entry.ReceiverSurface)) &&
        (entry.ReceiverCompletedScope == entry.ReceiverScope) && !entry.ReceiversPending);
    private static bool ReceiversPending(Entry entry) => (entry.ReceiversPending && HasIndirectReaders(entry: entry) &&
        (entry.View is { LightView: false, Residency.Tables.Indirect: { Frozen: false } }));

    private sealed partial class Entry {
        public long ReceiverSurface;
        public SdfIndirectReceiverSurface? ReceiverRecordedSurface;
        public SdfIndirectReceiverSurface? ReceiverSubmittedSurface;
        public SdfIndirectReceiverScope ReceiverScope;
        public SdfIndirectReceiverScope ReceiverCompletedScope;
        public bool ReceiversPending;
        public uint? ReaderCount;
        public long ReaderCompletedSurface = -1;
        public (uint Width, uint Height) ReceiverDemandExtent;
    }
}
