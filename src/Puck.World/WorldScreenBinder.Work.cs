using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    private readonly WorldRenderProbe? m_renderProbe;

    // Every view name registered with the render probe, so disposal withdraws each one.
    private readonly HashSet<string> m_workViews = new(comparer: StringComparer.Ordinal);
    // The moved sets of the views that compose a frame source of their own (a session view's), by view name, each
    // attached to the probe's sdf.transforms forwarder until the view is released.
    private readonly Dictionary<string, SdfMovedTransforms> m_workTransforms = new(comparer: StringComparer.Ordinal);

    // Registers (or re-registers) a view's GPU work with the render probe under the view's stack name. A view that
    // composes its own frame source passes that source's moved set, which world.counters then sums into
    // sdf.transforms beside the presenter's.
    private void RegisterViewWork(string name, IGpuWorkSource work, IWorkCounterSource lifetime, SdfMovedTransforms? transforms = null) {
        if (m_renderProbe is not { } probe) {
            return;
        }

        probe.RegisterView(
            lifetime: lifetime,
            name: name,
            work: work
        );
        _ = m_workViews.Add(item: name);
        DetachTransforms(name: name);

        if (transforms is not null) {
            probe.Transforms.Attach(instance: transforms);
            m_workTransforms[name] = transforms;
        }
    }
    // Releases a view from the stack (which disposes it) and withdraws its GPU work from the render probe.
    private void ReleaseView(string name) {
        m_viewStack?.Release(name: name);

        if (m_workViews.Remove(item: name)) {
            m_renderProbe?.UnregisterView(name: name);
        }

        DetachTransforms(name: name);
    }
    private void UnregisterAllViewWork() {
        foreach (var name in m_workViews) {
            m_renderProbe?.UnregisterView(name: name);
        }

        m_workViews.Clear();

        foreach (var transforms in m_workTransforms.Values) {
            m_renderProbe?.Transforms.Detach(instance: transforms);
        }

        m_workTransforms.Clear();
    }
    // Retires a view's moved set from sdf.transforms, carrying its totals forward.
    private void DetachTransforms(string name) {
        if (m_workTransforms.Remove(
            key: name,
            value: out var transforms
        )) {
            m_renderProbe?.Transforms.Detach(instance: transforms);
        }
    }
}
