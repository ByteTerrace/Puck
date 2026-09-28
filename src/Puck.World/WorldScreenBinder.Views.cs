using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

// The views the world renders beside its own, as the render graph runs them: each camera registration and each session
// screen is an sdf.world instance (WorldViewInstances). A camera view is a view of the world's own frame, rendered from
// the world's residency (FilmViews); a session screen renders a residency of its own, which the binder creates when the
// render graph's package first resolves the instance and releases once the session is gone. The binder hands the
// instances to its mappings, which the view graph host composes into the running set.
internal sealed partial class WorldScreenBinder {
    // The kernels every view's residency records with, read once from the composition's pipeline cache.
    private SdfKernelSet? m_viewKernels;
    // What the views are composed with: the host world's frame source, whose glyph atlas, decals and moving screens a
    // camera view shares, and the display's extent, which a view's declared extent is a fraction of.
    private ISdfFrameSource? m_viewHostSource;

    private int m_viewDisplayHeight = 1;
    private int m_viewDisplayWidth = 1;

    // The seats' views for the frame just dressed, whose primary render camera a window fits its eye to.
    private WorldSeatViewports? m_viewports;
    // The frame being prepared, through which a session view captures the world's frame before its own.
    private FrameContext m_frameContext;
    private bool m_hasFrameContext;
    // The simulation tick the frame the world renders presents, which a camera rig's clock reads.
    private ulong m_viewAuthoritativeTick;

    // The views as the render graph last received them, set again whenever a registration, a screen or a session moves.
    private readonly WorldViewSet m_views = new();
    // Each session view's residency by instance name, and the frame source it renders; released views reused.
    private readonly Dictionary<string, ViewResidency> m_viewResidencies = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_releasedViews = [];

    /// <summary>Gets or sets the world's residency, which renders every camera view as a view of the world's frame, and
    /// whose frame each routed residency films first; <see langword="null"/> in a presentation with no render graph, where
    /// no view renders.</summary>
    public SdfWorldResidency? ViewHost { get; set; }

    /// <summary>Hands the binder the frame the world renders once the presenter has captured it: its packed transforms and
    /// simulation tick, which the camera views filmed it with (<see cref="FilmViews"/>). The routed residencies no scene
    /// presents any longer are released, and an armed crossing capture is served.</summary>
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
    /// <summary>Returns the view a view instance renders: a camera registration's view of the world's frame, or a session
    /// screen's residency, created the first time the render graph's package asks for it once the views are
    /// configured.</summary>
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
            (m_viewHostSource is null)
        ) {
            return false;
        }

        if (m_cameraViews.ContainsKey(key: name)) {
            if (ViewHost is not { } world) {
                return false;
            }

            // Before the first dress, view zero gives builds the world's tables. BeginFrame refreshes the binding
            // after the host films its cameras and before any pass records.
            view = new SdfWorldView(
                Residency: world,
                View: m_cameraViewIndices.GetValueOrDefault(key: name)
            );

            return true;
        }

        if (SessionFeedOf(name: name) is not { FrameSource: { } source } feed) {
            return false;
        }

        // A window joined to its destination's endpoint scene renders its view there, from the one residency that scene's
        // seats and windows share; until the presenter's latch includes it, it renders its own session.
        if (
            (RoutedWindowOf(feed: feed) is { } window) &&
            TryResolveWindowView(
                view: out view,
                window: window
            )
        ) {
            return true;
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

    // A session screen's residency, rendering the session feed's frame source on its own clock.
    private ViewResidency CreateSessionResidency(string name, SdfCompositionFrameSource source) {
        var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            dynamicTransformCapacity: source.WorstCaseDynamicTransformCapacity,
            frameSource: new WorldSessionFrameSource(
                captureHostFirst: CaptureHostFirst,
                inner: source
            ),
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
    private SdfKernelSet ViewKernels() =>
        (m_viewKernels ??= m_viewPipelines!.LoadDeployed(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: m_viewHostsOnDirectX)));
    // Releases every session residency whose frame source differs from its session's: the view's instance leaves the
    // render graph, whose passes give back their holds once the device
    // has finished with them.
    private void ReconcileViewResidencies() {
        m_releasedViews.Clear();

        foreach (var (name, view) in m_viewResidencies) {
            if (
                (SessionFeedOf(name: name) is not { } feed) ||
                !ReferenceEquals(
                    objA: feed.FrameSource,
                    objB: view.Source
                ) ||
                (RoutedWindowOf(feed: feed) is not null)
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
