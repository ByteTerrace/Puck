using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Overlays;
using Puck.SdfVm;

namespace Puck.World;

/// <summary>
/// A mutable singleton holder for the live render nodes, so console verbs can read them without depending on the
/// render composition. Program's <see cref="Puck.Hosting.IRenderNode"/> factory stores the built producer, the
/// overlay, and the render host here; each is <see langword="null"/> until the renderer is built on the first frame.
/// It is also the <see cref="IGpuWorkRegistry"/> whose nodes the <c>gpu</c> section of <c>world.counters</c> reports:
/// the engine node, its hosted children that count their work, the overlay, and the offscreen views
/// <see cref="WorldScreenBinder"/> registers. It is the host's <see cref="IWorldEngineReadiness"/> too: the engine node's
/// readiness, not ready until the render factory has composed that node.
/// </summary>
internal sealed class WorldRenderProbe : IGpuWorkRegistry, IWorldEngineReadiness {
    private readonly Lock m_gate = new();
    private readonly List<WorkEntry> m_views = [];

    /// <summary>The <c>sdf.transforms</c> counters as <c>world.counters</c> reads them: registered with the probe before
    /// the frame presenter exists, and pointed at the presenter's moved set when the presenter is built, so reading the
    /// counters never builds the presenter early. Each session view composes a frame source of its own, whose moved set
    /// is attached while the view is registered and carried forward when it is released, so the counters are the sum
    /// of every frame source the host packs.</summary>
    public ForwardingWorkCounterSource Transforms { get; } = new(
        kinds: SdfMovedTransforms.Kinds,
        name: SdfMovedTransforms.SourceName
    );

    /// <summary>The device the render nodes run on, or <see langword="null"/> until the render factory has run.</summary>
    public IGpuDeviceContext? Device { get; set; }
    /// <inheritdoc/>
    public GpuDeviceIdentity? DeviceIdentity =>
        Device?.Identity;
    /// <inheritdoc/>
    public GpuDeviceCapabilities? DeviceCapabilities =>
        Device?.Capabilities;
    /// <summary>The SDF engine node the render root wraps, or <see langword="null"/> until the render factory has run.</summary>
    public SdfEngineNode? Node { get; set; }
    /// <inheritdoc/>
    public bool IsReady => (Node?.IsReady ?? false);
    /// <inheritdoc/>
    public string? NotReadyReason => ((Node is { } node)
        ? node.NotReadyReason
        : "the renderer has not been composed: no frame has been produced"
    );
    /// <summary>The unified overlay decorator, or <see langword="null"/> when the overlay was not composed —
    /// <c>world.counters gpu</c> reports its pass beside the engine's.</summary>
    public UnifiedOverlayNode? Overlay { get; set; }
    /// <summary>The assembled render host, or <see langword="null"/> until the render factory has run — the
    /// <c>world.screenshot</c> verb arms captures through its <see cref="SdfWorldRender.RequestCapture"/> (which
    /// routes to the OUTERMOST decorator, so the readback lands on the final composed frame).</summary>
    public SdfWorldRender? Render { get; set; }

    /// <inheritdoc/>
    /// <remarks>The engine node reads as <c>world</c>, its hosted children that count their work by their registered
    /// names, the overlay as <c>overlay</c>, and each registered view as <c>view:&lt;name&gt;</c>.</remarks>
    public void CopyNodes(List<GpuWorkNode> nodes) {
        ArgumentNullException.ThrowIfNull(nodes);

        if (Node is { } node) {
            nodes.Add(item: new GpuWorkNode(
                Lifetime: node.WorkLifetime,
                Name: "world",
                Work: node.Work
            ));

            foreach (var (name, child) in node.Children) {
                if (child is IGpuWorkSource childWork) {
                    nodes.Add(item: new GpuWorkNode(
                        Lifetime: (child as IWorkCounterSource),
                        Name: name,
                        Work: childWork
                    ));
                }
            }
        }

        if (Overlay is { } overlay) {
            nodes.Add(item: new GpuWorkNode(
                Lifetime: overlay.WorkLifetime,
                Name: "overlay",
                Work: overlay.Work
            ));
        }

        lock (m_gate) {
            foreach (var view in m_views) {
                nodes.Add(item: new GpuWorkNode(
                    Lifetime: view.Lifetime,
                    Name: $"view:{view.Name}",
                    Work: view.Work
                ));
            }
        }
    }
    /// <summary>Registers, or replaces under the same name, an offscreen view's GPU work.</summary>
    /// <param name="name">The view's registered name.</param>
    /// <param name="work">The view's completed-work source.</param>
    /// <param name="lifetime">The view's object-lifetime counters.</param>
    public void RegisterView(string name, IGpuWorkSource work, IWorkCounterSource lifetime) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(lifetime);

        lock (m_gate) {
            RemoveView(name: name);
            m_views.Add(item: new WorkEntry(
                Lifetime: lifetime,
                Name: name,
                Work: work
            ));
        }
    }
    /// <summary>Removes a view's GPU work; a name never registered is ignored.</summary>
    /// <param name="name">The view's registered name.</param>
    public void UnregisterView(string name) {
        lock (m_gate) {
            RemoveView(name: name);
        }
    }

    private void RemoveView(string name) =>
        _ = m_views.RemoveAll(match: entry => string.Equals(
            a: entry.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));

    private readonly record struct WorkEntry(string Name, IGpuWorkSource Work, IWorkCounterSource Lifetime);
}
