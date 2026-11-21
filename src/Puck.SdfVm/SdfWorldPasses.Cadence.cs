using Puck.Hosting;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    internal ulong? SignatureOf(string instance, string part, bool temporal, in FrameContext context, RenderGraphExternalReads? reads) {
        var entry = Refresh(instance: instance);

        if (entry.View is not { } view) { return null; }
        Begin(residency: view.Residency);
        var tables = view.Residency.Staged(context: in context);
        var frame = view.Residency.Frame!;

        // The distant map stands while its own inputs stand, whatever forces the frame's other passes
        // (SdfWorldPasses.Distant).
        if (part == SdfWorldPackage.Parts.ShadowDistant) {
            _ = CascadesOf(frame: frame, instance: instance, tables: tables, view: view.View);

            return ((entry.DistantDecided == m_frame) ? DistantSignatureOf(entry: entry, frame: frame, tables: tables) : null);
        }
        // Asked ahead of every early answer below, so a held or forced pass keeps its scope held.
        var motion = MotionOf(entry: entry, instance: instance, part: part, tables: tables, view: view);

        // A held far cascade stands whatever forces the frame's other passes (SdfWorldPasses.Rates).
        if ((part == SdfWorldPackage.Parts.ShadowFar) && HoldsFar(frame: frame, instance: instance, tables: tables, view: view.View)) {
            return FarSignatureOf(entry: entry, signature: null);
        }
        if (!frame.EnableCadenceGate || entry.Picker.Pending || (entry.Convergence is { IsActive: true }) ||
            (ReadSignatureOf(entry: entry, frame: frame, part: part, reads: reads, view: view) is not { } images)) {
            SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.Forced);
            return ((part == SdfWorldPackage.Parts.ShadowFar) ? FarSignatureOf(entry: entry, signature: null) : null);
        }
        UpdateSurfaceInputs(entry: entry, instance: instance, tables: tables, view: view);
        if (entry.CadenceFrame != m_frame) {
            entry.CadenceFrame = m_frame;
            // The upload has run, so the scope's poses are the ones this frame's tables hold: the history continues
            // only while they are the poses its latest render read, which a draw moving outside the view leaves so.
            if (!entry.Temporal.Stands(epoch: EpochOf(entry: entry, view: view, width: entry.Temporal.Epoch.Width,
                height: entry.Temporal.Epoch.Height, debug: tables.PassValues.DebugMode, temporal: temporal,
                unread: entry.UnreadFrames), previousPoses: PosesOf(entry: entry, tables: tables).Poses)) {
                entry.SampleRevision++;
                SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.Resampled);
            }
        }
        var hash = Fnv1aHash.Create();

        hash.Add(value: tables.PassSignature(frame: frame, motion: motion, part: part, view: Math.Min(val1: view.View, val2: (frame.Views.Count - 1))));
        hash.Add(value: entry.Bindings);
        // A cascade is drawn from its light's camera, which no sample of the view's jitter moves, so a cascade stands
        // through the samples a temporal view takes of a still scene.
        if (DrawScopeOf(part: part) is not (DrawScope.ShadowNear or DrawScope.ShadowFar or DrawScope.ShadowSecond)) {
            hash.Add(value: entry.SampleRevision);
        }
        hash.Add(value: images);
        return ((part == SdfWorldPackage.Parts.ShadowFar) ? FarSignatureOf(entry: entry, signature: hash.Value) : hash.Value);
    }

    private void UpdateSurfaceInputs(Entry entry, string instance, SdfWorldView view, SdfWorldTables tables) {
        var frame = view.Residency.Frame!;
        var signature = tables.PassSignature(
            frame: frame,
            motion: MotionOf(entry: entry, instance: instance, part: SdfWorldPackage.Parts.Views, tables: tables, view: view),
            part: SdfWorldPackage.Parts.Views,
            view: Math.Min(val1: view.View, val2: (frame.Views.Count - 1))
        );

        if ((entry.SurfaceSignature != signature) || (entry.RenderedScale != entry.CurrentScale)) {
            entry.SurfaceSignature = signature;
            entry.Temporal.Changed();
        }
    }

    internal void MarkSampleRendered(string instance) => Refresh(instance: instance).SampleRenderedFrame = m_frame;
    internal SdfTemporalHistory SkyTemporalOf(string instance) => Refresh(instance: instance).Temporal;

    private sealed partial class Entry {
        public ulong? SurfaceSignature { get; set; }
        public long CadenceFrame { get; set; } = -1;
        public long SampleRevision { get; set; }
        // The plan a binned view's passes share (ShadePlanOf).
        public SdfShadeDispatch? ShadePlan { get; set; }
        public long SampleRenderedFrame { get; set; } = -1;
        public long UnreadFrames { get; set; }
    }
}
