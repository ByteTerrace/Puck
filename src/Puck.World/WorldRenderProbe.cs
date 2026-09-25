using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.Shaders;

namespace Puck.World;

/// <summary>
/// A mutable singleton holder for the live render nodes, so console verbs can read them without depending on the
/// render composition. The <see cref="Puck.Hosting.IRenderNode"/> factory stores the engine node and the render graph's
/// root here; each is <see langword="null"/> until the renderer is built on the first frame. It is also the
/// <see cref="IGpuWorkRegistry"/> whose nodes the <c>gpu</c> section of <c>world.counters</c> reports: the engine node,
/// its hosted children that count their work, each graph instance the render graph renders, and the offscreen views
/// <see cref="WorldScreenBinder"/> registers. It is the host's <see cref="IWorldEngineReadiness"/> too: ready once the
/// engine node is and the graph's root has a completed output rendered over it, not before the render factory has
/// composed either.
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
    public bool IsReady => (
        (Node?.IsReady ?? false) &&
        (Root?.Runtime.UnservedCaptureReason is null)
    );
    /// <inheritdoc/>
    /// <remarks>The engine node's reason while it builds, then the render graph's while its root has not rendered over
    /// a completed world output.</remarks>
    public string? NotReadyReason => (((Node is { } node) && (Root is { } root))
        ? (node.NotReadyReason ?? root.Runtime.UnservedCaptureReason)
        : "the renderer has not been composed: no frame has been produced"
    );
    /// <summary>The render graph's root, the render host every captured and presented frame comes from, or
    /// <see langword="null"/> until the render factory has run. <c>world.screenshot</c> arms captures on it, so the
    /// readback is the frame the display shows.</summary>
    public RenderGraphRuntimeNode? Root { get; set; }

    /// <summary>Returns the capture target of a render-graph instance: the root for <see langword="null"/>, else the
    /// instance the name names.</summary>
    /// <param name="instance">The instance a capture reads, or <see langword="null"/> for the root.</param>
    /// <returns>The target, or <see langword="null"/> until the render factory has run.</returns>
    /// <exception cref="ArgumentException">The render graph has no instance of that name.</exception>
    public ICaptureRequestTarget? CaptureTarget(string? instance) => ((Root is not { } root)
        ? null
        : ((instance is null)
            ? root
            : root.Runtime.CaptureTarget(instance: instance)));
    /// <inheritdoc/>
    /// <remarks>The engine node reads as <c>world</c>, each render-graph instance that renders a graph by its instance
    /// name (the root's passes are its post
    /// passes and the overlay), and each registered view as <c>view:&lt;name&gt;</c>.</remarks>
    public void CopyNodes(List<GpuWorkNode> nodes) {
        ArgumentNullException.ThrowIfNull(nodes);

        if (Node is { } node) {
            nodes.Add(item: new GpuWorkNode(
                Lifetime: node.WorkLifetime,
                Name: "world",
                Work: node.Work
            ));
        }

        if (Root?.Runtime is { } runtime) {
            for (var index = 0; (index < runtime.Instances.Instances.Count); index++) {
                if (runtime.Producer(instance: index) is not null) {
                    continue;
                }

                var instance = runtime.Node(instance: index);

                nodes.Add(item: new GpuWorkNode(
                    Lifetime: instance,
                    Name: runtime.Instances.Instances[index].Name,
                    Work: instance
                ));
            }
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
