using Puck.Hosting;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    internal ulong? SignatureOf(string instance, string part, bool temporal, in FrameContext context, RenderGraphExternalReads? reads) {
        var entry = Refresh(instance: instance);

        // The recorder's preparation began the residency's frame (PrepareRecord).
        if (entry.View is not { } view) { return null; }
        var tables = view.Residency.Staged(context: in context);
        var frame = view.Residency.Frame!;
        // Asked ahead of every early answer below, so a forced pass keeps its scope held.
        var motion = MotionOf(entry: entry, part: part, tables: tables, view: view);

        if (!frame.EnableCadenceGate || entry.Picker.Pending || (entry.Convergence is { IsActive: true }) ||
            (ReadSignatureOf(entry: entry, frame: frame, latch: context.Latch, part: part, reads: reads, tables: tables, view: view) is not { } images)) {
            SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.Forced);
            return null;
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

        hash.Add(value: tables.PassSignature(frame: frame, scopeLighting: LightingOf(entry: entry, part: part, tables: tables), motion: motion, part: part, view: Math.Min(val1: view.View, val2: (frame.Views.Count - 1))));
        hash.Add(value: entry.Bindings);
        hash.Add(value: entry.SampleRevision);
        hash.Add(value: images);
        return hash.Value;
    }

    private void UpdateSurfaceInputs(Entry entry, string instance, SdfWorldView view, SdfWorldTables tables) {
        var frame = view.Residency.Frame!;
        var signature = tables.PassSignature(
            frame: frame,
            motion: MotionOf(entry: entry, part: SdfWorldPackage.Parts.Views, tables: tables, view: view),
            scopeLighting: LightingOf(entry: entry, part: SdfWorldPackage.Parts.Views, tables: tables),
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
