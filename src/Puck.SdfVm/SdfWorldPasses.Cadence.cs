using Puck.Hosting;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    internal ulong? SignatureOf(string instance, string part, bool temporal, in FrameContext context) {
        var entry = Refresh(instance: instance);

        if (entry.View is not { } view) { return null; }
        Begin(residency: view.Residency);
        if (view.LightView) {
            view.Residency.PlanLightView(context: in context);
            return view.Residency.IndirectLightViews.Revision;
        }
        var tables = view.Residency.Submit(context: in context);
        var frame = view.Residency.Frame!;

        if ((part == SdfWorldPackage.Parts.Views) && ReceiversPending(entry: entry)) { return null; }
        if (tables.ForcesPass(frame: frame, part: part) || entry.Picker.Pending || (entry.Convergence is { IsActive: true })) { return null; }
        UpdateSurfaceInputs(entry: entry, tables: tables, view: view);
        if (entry.CadenceFrame != m_frame) {
            entry.CadenceFrame = m_frame;
            if (!entry.Temporal.Stands(epoch: EpochOf(entry: entry, view: view, width: entry.Temporal.Epoch.Width,
                height: entry.Temporal.Epoch.Height, debug: tables.PassValues.DebugMode, temporal: temporal,
                unread: entry.UnreadFrames), previousPoses: tables.PoseRevision)) {
                entry.SampleRevision++;
            }
        }
        var hash = Fnv1aHash.Create();

        hash.Add(value: tables.PassSignature(frame: frame, view: Math.Min(val1: view.View, val2: (frame.Views.Count - 1)), part: part));
        hash.Add(value: entry.Bindings);
        hash.Add(value: entry.SampleRevision);
        if ((part == SdfWorldPackage.Parts.Views) && (LightViewName(residency: view.Residency) is not null)) {
            hash.Add(value: view.Residency.IndirectLightViews.Revision);
        }
        return hash.Value;
    }

    private static void UpdateSurfaceInputs(Entry entry, SdfWorldView view, SdfWorldTables tables) {
        var frame = view.Residency.Frame!;
        var signature = tables.PassSignature(frame: frame, view: Math.Min(val1: view.View, val2: (frame.Views.Count - 1)), part: SdfWorldPackage.Parts.Views);

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
        public long SampleRenderedFrame { get; set; } = -1;
        public long UnreadFrames { get; set; }
    }
}
