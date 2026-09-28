using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// A mutable singleton holder for the live render objects, so console verbs can read them without depending on the
/// render composition. The render root's factory stores the world's SDF residency and the render graph's root here; each
/// is <see langword="null"/> until the renderer is built on the first frame. It is also the
/// <see cref="IGpuWorkRegistry"/> whose nodes the <c>gpu</c> section of <c>world.counters</c> reports: each graph
/// instance the render graph renders by its instance name, the world's residency's uploads as <c>sdf:world</c>, and
/// each camera and session view's as <c>sdf:&lt;name&gt;</c>, which <see cref="WorldScreenBinder"/> registers. It is the
/// host's <see cref="IWorldEngineReadiness"/> too: ready once the world's residency has built its tables and the
/// graph's root has a completed output rendered over it, not before the render factory has composed either, and once
/// the bake schedule has reconciled, so whether the presentation draws its bakes is known, and, while it draws them
/// (<see cref="WorldRenderSettings.DrawsBakes"/>), is settled, so a capture or a <c>world.wait ready</c> never lands
/// between a placement's field and its bake.
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

    /// <summary>Actual timeline work across primary, routed and session frames. Retiring views carry their totals
    /// forward through the same counter mechanism as dynamic transforms.</summary>
    public ForwardingWorkCounterSource Timeline { get; } = new(WorldEnvironmentResolve.SourceName, WorldEnvironmentResolve.Kinds);

    /// <summary>The device the render nodes run on, or <see langword="null"/> until the render factory has run.</summary>
    public IGpuDeviceContext? Device { get; set; }
    /// <inheritdoc/>
    public GpuDeviceIdentity? DeviceIdentity =>
        Device?.Identity;
    /// <inheritdoc/>
    public GpuDeviceCapabilities? DeviceCapabilities =>
        Device?.Capabilities;
    /// <summary>The world's SDF residency, or <see langword="null"/> until the render factory has run.</summary>
    public SdfWorldResidency? Residency { get; set; }
    /// <inheritdoc/>
    public bool CapturesSettled => (Root?.PendingCapturePath is null);
    /// <inheritdoc/>
    public bool IsReady => (
        (Residency?.IsReady ?? false) &&
        (Root?.Runtime.UnservedCaptureReason is null) &&
        BakesSettled
    );
    /// <inheritdoc/>
    /// <remarks>The world's residency's reason while it builds, then the render graph's while its root has not rendered
    /// over a completed world output, then the bake schedule's while a bake the presentation would draw is still
    /// baking.</remarks>
    public string? NotReadyReason => (((Residency is { } residency) && (Root is { } root))
        ? (residency.NotReadyReason ?? (root.Runtime.UnservedCaptureReason ?? (BakesSettled
            ? null
            : "the creation bakes are settling: a prototype the presentation would draw baked is still queued or baking")))
        : "the renderer has not been composed: no frame has been produced"
    );
    /// <summary>The presentation's bake schedule, or <see langword="null"/> until the render factory has run.</summary>
    public WorldBakeSchedule? Bakes { get; set; }
    /// <summary>The presentation's render settings, whose <see cref="WorldRenderSettings.DrawsBakes"/> says whether it
    /// draws its bakes, or <see langword="null"/> until the render factory has run.</summary>
    public WorldRenderSettings? Settings { get; set; }

    // A schedule must reconcile before readiness can decide whether it draws bakes, and settle while it draws them.
    private bool BakesSettled => ((Settings is not { } settings) || (Bakes is not { } bakes) || bakes.IsReadyForDrawing(bakes: settings.Bakes));

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
    /// <remarks>The world's residency's uploads read as <c>sdf:world</c>, each render-graph instance that renders a graph
    /// by its instance name (an SDF view's passes, <c>sdf.world$sky</c> through <c>sdf.world$views</c>; the root's, its
    /// place and post passes and the overlay), and each registered view's residency as <c>sdf:&lt;name&gt;</c>.</remarks>
    public void CopyNodes(List<GpuWorkNode> nodes) {
        ArgumentNullException.ThrowIfNull(nodes);

        if (Residency is { } residency) {
            nodes.Add(item: new GpuWorkNode(
                Lifetime: residency.WorkLifetime,
                Name: ResidencyNode(name: residency.Name),
                Work: residency.Work
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
                    Name: ResidencyNode(name: view.Name),
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

    // The work node a residency's uploads read as.
    private static string ResidencyNode(string name) => $"sdf:{name}";
    private void RemoveView(string name) =>
        _ = m_views.RemoveAll(match: entry => string.Equals(
            a: entry.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));

    private readonly record struct WorkEntry(string Name, IGpuWorkSource Work, IWorkCounterSource Lifetime);
}
