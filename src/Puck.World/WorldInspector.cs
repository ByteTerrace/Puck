using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Publishes the same per-seat inspector text to the existing overlay and console.</summary>
internal sealed class WorldInspector(WorldEditorSeats seats, WorldCursorFeed cursor, WorldSeatViewports viewports,
    WorldClient client, WorldViewGraphHost views, WorldRenderProbe probe, WorldSourceWatch watch, FrameRateMonitor fps, WorldGpuTiming timing, WorldSeatAuthorityRouter? router = null) : IInspectorSource {
    private readonly WorldInspectorText[] m_text = Enumerable.Range(count: PlayerRoster.MaxSlots, start: 0).Select(selector: static _ => new WorldInspectorText()).ToArray();
    private readonly List<GpuWorkNode> m_nodes = [];
    private readonly GpuWorkSample m_sample = new();

    public void Tick() {
        for (var slot = 0; (slot < m_text.Length); slot++) {
            if (seats.InspectorEnabled(slot: slot)) { Refresh(slot: slot); }
        }
    }
    public ReadOnlySpan<char> Read(int slot, out NormalizedRect viewport) {
        var view = viewports.Seat(slot: slot);

        viewport = view.Region;
        return (seats.InspectorEnabled(slot: slot) ? m_text[slot].Text : []);
    }
    public string Describe(int slot) {
        Refresh(slot: slot);
        return new string(value: m_text[slot].Text);
    }
    public SdfWorldResidency? ResidencyOf(int slot) => ViewOf(slot: slot)?.Residency;

    private string InstanceOf(int slot) => (((cursor.Status.Slot == slot) ? cursor.PickInstance : null) ?? (viewports.Seat(slot: slot).RenderInstance ?? WorldViewGraphs.WorldInstance));
    private SdfWorldView? ViewOf(int slot) => (((cursor.Status.Slot == slot) ? cursor.PickView : null) ?? views.FindPicker(instance: InstanceOf(slot: slot))?.View);
    private long Count(int column) {
        var total = m_sample.GetOutsidePassCount(column: column);

        for (var pass = 0; (pass < m_sample.PassCount); pass++) {
            if (m_sample.TryGetPassCount(column: column, pass: pass, value: out var value)) { total += value; }
        }
        return total;
    }
    private void Refresh(int slot) {
        m_nodes.Clear();
        probe.CopyNodes(nodes: m_nodes);
        long dispatches = 0, uploads = 0, created = 0;
        var dispatchColumn = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.Dispatches);
        var uploadColumn = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.HostVisibleUploadBytes);

        foreach (var node in m_nodes) {
            if (node.Work.TryReadCompleted(sample: m_sample)) {
                dispatches += Count(column: dispatchColumn);
                uploads += Count(column: uploadColumn);
            }
            if (node.Lifetime is { } lifetime) {
                foreach (var kind in lifetime.WorkKinds) {
                    if (lifetime.TryRead(kind: kind, value: out var value)) { created += value; }
                }
            }
        }
        var followed = ViewOf(slot: slot);
        var residency = followed?.Residency;
        var rendered = (((followed is { } followedView) && (residency?.Frame is { } frame) && (((uint)followedView.View) < ((uint)frame.Views.Count))) ? frame.Views[followedView.View] : (SdfViewSnapshot?)null);
        var route = router?.TryRoute(slot: slot);
        var mirror = (route?.Endpoint.FollowState() ?? client.StateMirror);
        var pick = ((cursor.Status.Slot == slot) ? cursor.Pick : null);
        var snapshot = new WorldInspectorSnapshot {
            Definition = (route?.Endpoint.Definition ?? client.Definition),
            Mirror = mirror,
            Slot = slot,
            Pick = pick,
            IndirectReference = cursor.ReferenceOf(pick: pick),
            Camera = (pick?.Sample?.Camera ?? viewports.Seat(slot: slot).Camera),
            Selection = seats.CurrentOf(slot: slot, world: (route?.Endpoint.Identity ?? WorldInstanceHost.BootInstanceName)),
            SimulationTick = mirror.Tick,
            PresentationTick = mirror.EngineTick,
            Settings = probe.Settings,
            View = rendered,
            DebugMode = (residency?.DebugMode ?? 0),
            Dispatches = dispatches,
            Uploads = uploads,
            Created = created,
            Words = (residency?.LiveProgramWords ?? 0),
            WordCapacity = (residency?.ProgramWordCapacity ?? 0),
            Instances = (residency?.LiveProgramInstances ?? 0),
            ReloadError = (watch.LastError ?? "none"),
            WorldRoot = client.Definition.DocumentDirectory,
        };
        var text = m_text[slot];

        text.Format(snapshot: in snapshot);
        if (timing.ReadoutEnabled && (probe.Root?.Runtime is { } runtime)) {
            var observed = fps.Summarize();

            text.FrameRate(mean: observed.AverageFps, slowest: observed.WorstFps);
            for (var index = 0; (index < runtime.Instances.Instances.Count); index++) {
                if ((runtime.Producer(instance: index) is not null) || (runtime.Instances.Instances[index].Name != InstanceOf(slot: slot))) { continue; }
                if (runtime.Node(instance: index).TimingRefusal is { } refusal) {
                    text.TimingRefused(node: runtime.Instances.Instances[index].Name, refusal: refusal);
                    continue;
                }
                foreach (var value in runtime.Node(instance: index).Timings) {
                    text.Timing(node: runtime.Instances.Instances[index].Name, timing: in value);
                }
            }
        }
        text.Finish();
    }
}
