using System.Diagnostics.CodeAnalysis;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

// The views the world renders beside its own, as the render graph runs them: each camera registration and each session
// screen is an sdf.world instance (WorldViewInstances), whose producer renders it through an engine node of its own. The
// binder hands the instances to its mappings, which the view graph host composes into the running set.
internal sealed partial class WorldScreenBinder {
    // The kernels every view's engine records with, read once from the composition's pipeline cache.
    private SdfWorldKernels? m_viewKernels;
    // What the views are composed with: the host world's frame source, whose glyph atlas, decals and moving screens a
    // camera view shares, and the display's extent, which a view's declared extent is a fraction of.
    private ISdfFrameSource? m_viewHostSource;
    private int m_viewDisplayHeight = 1;
    private int m_viewDisplayWidth = 1;
    // The simulation tick the frame the world node renders presents, which a camera rig's clock reads.
    private ulong m_viewAuthoritativeTick;

    /// <summary>Gets or sets the world's engine node, whose frame (<see cref="SdfEngineNode.HostFrame"/>) every camera view
    /// films; <see langword="null"/> in a presentation with no render graph, where no view renders.</summary>
    public SdfEngineNode? ViewHost { get; set; }

    /// <summary>Hands the binder what the frame the world node renders presents, as the presenter captures it: the packed
    /// transforms a camera's entity-part and seat anchors resolve against, and the simulation tick its rig's clock
    /// reads.</summary>
    /// <param name="transforms">The frame's packed dynamic transforms.</param>
    /// <param name="authoritativeTick">The latest authoritative simulation tick available to presentation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transforms"/> is <see langword="null"/>.</exception>
    public void PresentFrame(DynamicTransform[] transforms, ulong authoritativeTick) {
        ArgumentNullException.ThrowIfNull(argument: transforms);

        m_viewTransforms = transforms;
        m_viewAuthoritativeTick = authoritativeTick;
    }
    /// <summary>Creates the external producer of a view instance: a camera registration's or a session screen's.</summary>
    /// <param name="context">The instance.</param>
    /// <param name="producer">The producer, which the render graph owns, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the instance is a view this binder registered and the views are
    /// configured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public bool TryViewProducer(RenderGraphExternalProducerContext context, [NotNullWhen(returnValue: true)] out IRenderGraphExternalProducer? producer) {
        ArgumentNullException.ThrowIfNull(argument: context);

        producer = null;

        if (
            (m_viewPipelines is null) ||
            (m_viewHostSource is not { } host)
        ) {
            return false;
        }
        if (m_cameraViews.ContainsKey(key: context.Instance)) {
            producer = new CameraViewProducer(
                binder: this,
                host: host,
                name: context.Instance
            );

            return true;
        }
        if (SessionFeedOf(name: context.Instance) is not null) {
            producer = new SessionViewProducer(
                binder: this,
                name: context.Instance
            );

            return true;
        }

        return false;
    }

    // An engine node for one view: one viewport, no brick pool (a view never bakes carves, and the default pool would cost
    // megabytes per view; a filmed sampled region renders through the kernels' uncarved-hull fallback), sized to the
    // world's worst-case capacities, its extent the one the render graph schedules, and its work counted under the view's
    // name in world.counters.
    private SdfEngineNode CreateViewNode(string name, ISdfFrameSource frameSource, ISdfScreenSources? screenSources, IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? screenSurfaceTransforms, int? programWordCapacity = null, int? instanceCapacity = null, int? dynamicTransformCapacity = null) {
        var pipelines = m_viewPipelines!;
        var node = new SdfEngineNode(
            brickPoolVoxelCapacity: 0,
            debugLabel: $"view:{name}",
            dynamicTransformCapacity: (dynamicTransformCapacity ?? m_viewDynamicTransformCapacity),
            frameSource: frameSource,
            height: 1U,
            instanceCapacity: (instanceCapacity ?? m_viewInstanceCapacity),
            kernels: (m_viewKernels ??= pipelines.LoadDeployed(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: m_viewHostsOnDirectX))),
            pipelines: pipelines,
            programWordCapacity: (programWordCapacity ?? m_viewProgramWordCapacity),
            screenSources: screenSources,
            screenSurfaceTransforms: screenSurfaceTransforms,
            viewportCapacity: 1,
            width: 1U
        );

        return node;
    }
    // The view instance's running producer, or null when the render graph runs no instance of that name.
    private IRenderGraphExternalProducer? ViewProducerOf(string name) {
        if (Runtime is not { } runtime) {
            return null;
        }

        var index = runtime.Instances.IndexOf(name: name);

        return ((index < 0)
            ? null
            : runtime.Producer(instance: index));
    }
    // Rebuilds the view instances from the camera registrations and the session screens and hands them to the mappings
    // when they differ from the ones the mappings hold, or always when forced, which composes the running set again and so
    // restarts its scheduling history: every view something shows renders on the next frame.
    private void ReconcileViews(bool force = false) {
        var views = new List<WorldView>();
        var refresh = RenderGraphRefresh.Every(divisor: m_viewRefreshDivisor);

        foreach (var name in m_cameraViews.Keys.Order(comparer: StringComparer.Ordinal)) {
            var registration = m_cameraViews[name];
            var exported = (
                (registration.Seat == DefaultViewSeat) &&
                HasViewExportReferences(cameraName: registration.Row.Name)
            );
            var demand = ((WiredScreensFor(name: name).Count > 0)
                ? WorldViewDemand.Screen
                : ((exported || (HasRetainedView(registrationName: name) && !m_parkedViews.Contains(item: name)))
                    ? WorldViewDemand.Root
                    : WorldViewDemand.None));

            views.Add(item: new WorldView(
                Demand: demand,
                FilmsWorld: true,
                Height: ViewFraction(
                    display: m_viewDisplayHeight,
                    pixels: ((int)registration.Row.RenderHeight)
                ),
                Name: name,
                Refresh: refresh,
                Width: ViewFraction(
                    display: m_viewDisplayWidth,
                    pixels: ((int)registration.Row.RenderWidth)
                )
            ));
        }
        foreach (var index in m_slots.Keys.Order()) {
            if (m_slots[index].Session is not { FrameSource: not null } feed) {
                continue;
            }

            var resolution = (feed.Resolution ?? new WorldScreenResolution(
                Height: WorldViewInstances.DefaultSessionHeight,
                Width: WorldViewInstances.DefaultSessionWidth
            ));

            // A window renders every produced frame: a stale image between refreshes would show the destination lagging the
            // viewer's own eye, breaking the parallax the projection exists for.
            views.Add(item: new WorldView(
                Demand: WorldViewDemand.Screen,
                FilmsWorld: false,
                Height: ViewFraction(
                    display: m_viewDisplayHeight,
                    pixels: resolution.Height
                ),
                Name: feed.RegistrationName,
                Refresh: ((feed.Projection == WorldScreenProjection.Window)
                    ? RenderGraphRefresh.EveryFrame
                    : refresh),
                Width: ViewFraction(
                    display: m_viewDisplayWidth,
                    pixels: resolution.Width
                )
            ));
        }

        if (
            force ||
            !views.SequenceEqual(second: Mappings.Views.Views)
        ) {
            Mappings.ReconcileViews(views: WorldViewInstances.Of(views: views));
        }
    }
    // The fraction of a display axis a view's declared extent covers, at most the whole axis.
    private static double ViewFraction(int pixels, int display) => Math.Min(
        val1: 1.0,
        val2: (Math.Max(
            val1: 1,
            val2: pixels
        ) / ((double)Math.Max(
            val1: 1,
            val2: display
        )))
    );
}
