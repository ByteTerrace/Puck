using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

// The views the world renders beside its own, as the render graph runs them: each camera registration, session
// screen and available infinity layer is an sdf.world instance (WorldViewInstances). A camera view is a view of the world's own frame, rendered from
// the world's residency (FilmViews); a session screen renders a residency of its own, which the binder creates when the
// render graph's package first resolves the instance and releases once the session is gone. The binder hands the
// instances to its mappings, which the view graph host composes into the running set.
public sealed partial class WorldScreenBinder {
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
    /// presents any longer are released, and an armed crossing capture follows the pinned route and render completion.</summary>
    /// <param name="transforms">The frame's packed dynamic transforms.</param>
    /// <param name="authoritativeTick">The latest authoritative simulation tick available to presentation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transforms"/> is <see langword="null"/>.</exception>
    public void PresentFrame(DynamicTransform[] transforms, ulong authoritativeTick) {
        ArgumentNullException.ThrowIfNull(argument: transforms);

        m_viewTransforms = transforms;
        m_viewAuthoritativeTick = authoritativeTick;
        ReconcileRoutedResidencies();
        PresentCrossing();
    }
    /// <summary>Returns the view a view instance renders: a camera registration's view of the world's frame, a session
    /// screen's residency, created the first time the render graph's package asks for it once the views are configured,
    /// a fitted infinity layer, or a camera of another world's view of the residency that world renders through.</summary>
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

        if (TryResolveInfinityView(name: name, view: out view)) { return true; }

        if (SessionFeedOf(name: name) is not { } feed) {
            return TryResolveNestedCameraView(
                name: name,
                view: out view
            );
        }

        // A session the presentation routed renders as a view of a residency it shares; any other renders a residency of
        // its own.
        var resolved = ((feed.Home || (feed.Route.Window is not null))
            ? SharedSessionView(
                feed: feed,
                name: name
            )
            : new SdfWorldView(
                Residency: SessionResidencyOf(feed: feed),
                View: 0
            ));

        view = resolved.GetValueOrDefault();

        return resolved.HasValue;
    }
    /// <summary>Returns the view a render-graph instance of the world's package renders this frame: a view this binder
    /// registered (<see cref="TryResolveView"/>), the routed view of a seat presented in another world, or else one of the
    /// presentation's own views of the world's residency.</summary>
    /// <param name="name">The instance's name.</param>
    /// <param name="world">The world's residency.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="world"/> is
    /// <see langword="null"/>.</exception>
    public SdfWorldView ResolveInstanceView(string name, SdfWorldResidency world) {
        ArgumentNullException.ThrowIfNull(argument: world);

        if (TryResolveView(
            name: name,
            view: out var view
        )) {
            return view;
        }

        return (TryResolveRoutedView(
            name: name,
            view: out var routed
        )
            ? routed
            : new SdfWorldView(
                Residency: world,
                View: HostView(view: (WorldViewNames.ViewOf(instance: name) ?? 0))
            ));
    }

    // A registered session's own residency, rendering its frame source, created the first time a view resolves to it and
    // made again when its frame source is another.
    private SdfWorldResidency SessionResidencyOf(SessionFeed feed) {
        var source = OwnSourceOf(feed: feed);
        var name = feed.RegistrationName;

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
                feed: feed,
                name: name,
                source: source,
                resolution: (feed.Resolution ?? new WorldScreenResolution(Height: WorldViewInstances.DefaultSessionHeight, Width: WorldViewInstances.DefaultSessionWidth))
            );
            m_viewResidencies[name] = session;
        }

        return session.Residency;
    }
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
                feed.Home ||
                (feed.Route.Window is not null)
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
        ReleaseInfinityRoots();
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
            ) { OutputExtent = new RenderGraphPixelExtent(Width: ((int)registration.Row.RenderWidth), Height: ((int)registration.Row.RenderHeight)) });
        }
        EnsureFeeds();

        foreach (var feed in m_feeds) {
            if (!feed.Registered || !InfinityShown(feed: feed)) {
                continue;
            }

            var (width, height) = WorldViewInstances.Fit(
                displayHeight: m_viewDisplayHeight,
                displayWidth: m_viewDisplayWidth,
                height: (feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight),
                width: (feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth)
            );

            // A window renders every produced frame: a stale image between refreshes would show the destination lagging the
            // viewer's own eye, breaking the parallax the projection exists for. It renders at the extent its glass covers
            // in the view that shows it (WorldViewGraphHost's footprints), any other session at its resolution.
            var window = (feed.Projection == WorldScreenProjection.Window);

            m_views.Set(view: new WorldView(
                Demand: WorldViewDemand.Screen,
                FilmsWorld: false,
                Height: height,
                Name: feed.RegistrationName,
                Refresh: (window
                    ? RenderGraphRefresh.EveryFrame
                    : refresh),
                Width: width
            ) {
                OutputExtent = (window
                    ? null
                    : new RenderGraphPixelExtent(Width: (feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth), Height: (feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight))),
                Parent = (feed.ParentFeed?.RegistrationName ?? ((feed.ParentInfinity is { } infinity) ? InfinityParent(entry: infinity) : null)),
                Reads = feed.Nested?.Reads,
            });
        }

        SetNestedCameraViews(refresh: refresh);
        SetInfinityViews();
        m_views.SetNestedSources(sources: m_nestedSources);

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
