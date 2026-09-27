using Puck.Hosting;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

// The views the world renders beside its own, as the render graph runs them: each camera registration and each session
// screen is an sdf.world instance (WorldViewInstances) rendering a residency of its own, which the binder creates when the
// render graph's package first resolves the instance and releases once the registration or the session is gone. The
// binder hands the instances to its mappings, which the view graph host composes into the running set.
internal sealed partial class WorldScreenBinder {
    // The kernels every view's residency records with, read once from the composition's pipeline cache.
    private SdfKernelSet? m_viewKernels;
    // What the views are composed with: the host world's frame source, whose glyph atlas, decals and moving screens a
    // camera view shares, and the display's extent, which a view's declared extent is a fraction of.
    private ISdfFrameSource? m_viewHostSource;

    private int m_viewDisplayHeight = 1;
    private int m_viewDisplayWidth = 1;

    // The simulation tick the frame the world renders presents, which a camera rig's clock reads.
    private ulong m_viewAuthoritativeTick;

    // The views as the render graph last received them, set again whenever a registration, a screen or a session moves.
    private readonly WorldViewSet m_views = new();
    // Each view's residency by instance name, and for a session's the frame source it renders; released views reused.
    private readonly Dictionary<string, ViewResidency> m_viewResidencies = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_releasedViews = [];

    /// <summary>Gets or sets the world's residency, whose frame (<see cref="SdfWorldResidency.HostFrame"/>) every camera
    /// view films; <see langword="null"/> in a presentation with no render graph, where no view renders.</summary>
    public SdfWorldResidency? ViewHost { get; set; }

    /// <summary>Hands the binder what the frame the world renders presents, as the presenter captures it: the packed
    /// transforms a camera's entity-part and seat anchors resolve against, and the simulation tick its rig's clock
    /// reads.</summary>
    /// <param name="transforms">The frame's packed dynamic transforms.</param>
    /// <param name="authoritativeTick">The latest authoritative simulation tick available to presentation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transforms"/> is <see langword="null"/>.</exception>
    public void PresentFrame(DynamicTransform[] transforms, ulong authoritativeTick) {
        ArgumentNullException.ThrowIfNull(argument: transforms);

        m_viewTransforms = transforms;
        m_viewAuthoritativeTick = authoritativeTick;
        ReconcileRoutedResidencies();
        CrossingCapture?.Present();
    }
    /// <summary>Returns the view a view instance renders: a camera registration's or a session screen's residency, created
    /// the first time the render graph's package asks for it once the views are configured.</summary>
    /// <param name="name">The instance's name.</param>
    /// <param name="view">The view, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the instance is a view this binder registered and the views are
    /// configured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryResolveView(string name, out SdfWorldView view) {
        ArgumentNullException.ThrowIfNull(argument: name);

        view = default;

        if (
            (m_viewPipelines is null) ||
            (m_viewHostSource is not { } host)
        ) {
            return false;
        }

        if (m_cameraViews.ContainsKey(key: name)) {
            if (!m_viewResidencies.TryGetValue(
                key: name,
                value: out var camera
            )) {
                camera = CreateCameraResidency(
                    host: host,
                    name: name
                );
                m_viewResidencies[name] = camera;
            }

            view = new SdfWorldView(
                Residency: camera.Residency,
                View: 0
            );

            return true;
        }

        if (SessionFeedOf(name: name)?.FrameSource is not { } source) {
            return false;
        }

        if (
            !m_viewResidencies.TryGetValue(
                key: name,
                value: out var session
            ) ||
            !ReferenceEquals(
                objA: session.Source,
                objB: source
            )
        ) {
            ReleaseViewResidency(name: name);
            session = CreateSessionResidency(
                name: name,
                source: source
            );
            m_viewResidencies[name] = session;
        }

        view = new SdfWorldView(
            Residency: session.Residency,
            View: 0
        );

        return true;
    }

    // A camera view's residency: its frame source films the frame the world renders from the registration's camera, no
    // brick pool (a view never bakes carves, and the default pool would cost megabytes per view; a filmed sampled region
    // renders through the kernels' uncarved-hull fallback), sized to the world's worst-case capacities, and its work
    // counted under the view's name in world.counters. Low-resolution diegetic displays skip soft shadows and ambient
    // occlusion, which a view may add to the host's levers but never lift.
    private ViewResidency CreateCameraResidency(ISdfFrameSource host, string name) {
        var source = new SdfCameraFrameSource(host: host) {
            DisableAmbientOcclusion = true,
            DisableSoftShadows = true,
        };
        var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            dynamicTransformCapacity: m_viewDynamicTransformCapacity,
            film: context => FilmCamera(
                context: in context,
                name: name,
                source: source
            ),
            frameSource: source,
            height: 1U,
            instanceCapacity: m_viewInstanceCapacity,
            kernels: ViewKernels(),
            name: name,
            pipelines: m_viewPipelines!,
            programWordCapacity: m_viewProgramWordCapacity,
            screenSources: this,
            width: 1U
        );

        RegisterViewWork(
            lifetime: residency.WorkLifetime,
            name: name,
            work: residency.Work
        );

        return new ViewResidency(
            Residency: residency,
            Source: source
        );
    }
    // A session screen's residency, rendering the session feed's frame source on its own clock.
    private ViewResidency CreateSessionResidency(string name, SdfCompositionFrameSource source) {
        var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            dynamicTransformCapacity: source.WorstCaseDynamicTransformCapacity,
            frameSource: new SessionFrameSource(inner: source),
            height: WorldViewInstances.DefaultSessionHeight,
            instanceCapacity: source.WorstCaseInstanceCapacity,
            kernels: ViewKernels(),
            name: name,
            pipelines: m_viewPipelines!,
            programWordCapacity: source.WorstCaseProgramWordCapacity,
            width: WorldViewInstances.DefaultSessionWidth
        );

        RegisterViewWork(
            lifetime: residency.WorkLifetime,
            name: name,
            transforms: source.MovedTransforms,
            work: residency.Work
        );

        return new ViewResidency(
            Residency: residency,
            Source: source
        );
    }
    // Films a camera view's frame: the camera its registration resolves this frame against the frame the world renders,
    // and the export a probe reads it through, which the view's node copies into.
    private bool FilmCamera(in FrameContext context, string name, SdfCameraFrameSource source) {
        if (!TryFilm(
            camera: out var camera,
            context: in context,
            frame: out var frame,
            name: name,
            registration: out var registration
        )) {
            return false;
        }

        source.Camera = camera;
        source.HostFrame = frame;

        if (Runtime?.NodeOf(instance: name) is { } node) {
            node.Export = registration.Export;
        }

        return true;
    }
    private SdfKernelSet ViewKernels() =>
        (m_viewKernels ??= m_viewPipelines!.LoadDeployed(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: m_viewHostsOnDirectX)));
    // Releases the residency of every view that is no camera registration and no session with the frame source its
    // residency renders: the view's instance leaves the render graph, whose passes give back their holds once the device
    // has finished with them.
    private void ReconcileViewResidencies() {
        m_releasedViews.Clear();

        foreach (var (name, view) in m_viewResidencies) {
            if (
                !m_cameraViews.ContainsKey(key: name) &&
                !ReferenceEquals(
                    objA: SessionFeedOf(name: name)?.FrameSource,
                    objB: view.Source
                )
            ) {
                m_releasedViews.Add(item: name);
            }
        }

        foreach (var name in m_releasedViews) {
            ReleaseViewResidency(name: name);
        }
    }
    private void ReleaseViewResidency(string name) {
        if (!m_viewResidencies.Remove(
            key: name,
            value: out var view
        )) {
            return;
        }

        UnregisterViewWork(name: name);
        view.Residency.Dispose();
    }
    private void ReleaseViewResidencies() {
        foreach (var view in m_viewResidencies.Values) {
            view.Residency.Dispose();
        }

        m_viewResidencies.Clear();
        ReleaseRoutedResidencies();
    }
    // Sets every view from the camera registrations and the session screens and hands the views to the mappings when any
    // changed, which composes the running set again; a frame that changes nothing allocates nothing. A view's demand is
    // every way something shows it: a screen, and a HUD frame or a probe export.
    private void ReconcileViews() {
        var refresh = RenderGraphRefresh.Every(divisor: m_viewRefreshDivisor);

        m_views.Begin();

        foreach (var (name, registration) in m_cameraViews) {
            var demand = (IsWired(name: name)
                ? WorldViewDemand.Screen
                : WorldViewDemand.None);

            if (
                (
                    (registration.Seat == DefaultViewSeat) &&
                    HasViewExportReferences(cameraName: registration.Row.Name)
                ) ||
                (HasRetainedView(registrationName: name) && !m_parkedViews.Contains(item: name))
            ) {
                demand |= WorldViewDemand.Root;
            }

            var (width, height) = WorldViewInstances.Fit(
                displayHeight: m_viewDisplayHeight,
                displayWidth: m_viewDisplayWidth,
                height: ((int)registration.Row.RenderHeight),
                width: ((int)registration.Row.RenderWidth)
            );

            m_views.Set(view: new WorldView(
                Demand: demand,
                FilmsWorld: true,
                Height: height,
                Name: name,
                Refresh: refresh,
                Width: width
            ));
        }
        foreach (var slot in m_slots.Values) {
            if (slot.Session is not { FrameSource: not null } feed) {
                continue;
            }

            var (width, height) = WorldViewInstances.Fit(
                displayHeight: m_viewDisplayHeight,
                displayWidth: m_viewDisplayWidth,
                height: (feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight),
                width: (feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth)
            );

            // A window renders every produced frame: a stale image between refreshes would show the destination lagging the
            // viewer's own eye, breaking the parallax the projection exists for.
            m_views.Set(view: new WorldView(
                Demand: WorldViewDemand.Screen,
                FilmsWorld: false,
                Height: height,
                Name: feed.RegistrationName,
                Refresh: ((feed.Projection == WorldScreenProjection.Window)
                    ? RenderGraphRefresh.EveryFrame
                    : refresh),
                Width: width
            ));
        }

        if (m_views.TryPublish(instances: out var views)) {
            Mappings.ReconcileViews(views: views);
        }
    }
    // Whether any screen shows a camera registration.
    private bool IsWired(string name) {
        foreach (var slot in m_slots.Values) {
            if (string.Equals(
                a: slot.View?.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }

        return false;
    }

    // One view's residency and the frame source it renders.
    private sealed record ViewResidency(SdfWorldResidency Residency, object Source);
}
