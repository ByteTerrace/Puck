using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // The frame source a session renders through a residency of its own, built the first time it needs one: one
    // WorldSessionSceneEmitter composed through its own SdfCompositionFrameSource, which the session's instance renders
    // under the slot's own name, and the destination's render envelope sized by what the session discloses. Building it
    // measures the destination's whole scene, so a session that never renders its own residency never pays for it.
    private SdfCompositionFrameSource OwnSourceOf(SessionFeed feed) {
        if (feed.FrameSource is { } built) {
            return built;
        }

        var emitter = new WorldSessionSceneEmitter(
            domains: m_domains,
            mirror: feed.Mirror,
            effectiveCameraName: feed.EffectiveCamera,
            jobs: m_stamps.Jobs
        ) { SkyLayers = SkyLayers, Session = Settings, ViewName = feed.RegistrationName };
        var frameSource = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );

        feed.FrameSource = frameSource;
        feed.Emitter = emitter;
        feed.Content.Made();
        emitter.Film = views => FilmFeed(
            feed: feed,
            views: views
        );
        emitter.FitSkyViews = (views, sky, width, height) => FitSessionSky(feed: feed, height: height, sky: sky, views: views, width: width);
        emitter.SetWindowFit(fit: feed.WindowFit);
        emitter.ViewerPixelsPerMetre = () => (m_viewports?.ViewerPixelsPerMetre ?? 0f);
        // The session's frames take the presentation's levers, and its stamp pool packs by its rates and views.
        emitter.HostFrame = () => Presenter?.DressedFrame;
        emitter.PresentStamps = pool => Presenter?.PresentStamps(pool: pool);

        // The destination instance's own render envelope is not configured for a session screen by default — an
        // unconfigured envelope admits any document mutation regardless of capacity. Configuring it here closes
        // that gap for ordinary authored session screens. The candidate-aware emitter
        // measurement is load-bearing: returning the construction capacity for every candidate would make
        // WorldRenderEnvelope compare the ceiling to itself and admit every mutation.
        if (
            m_instanceHost.TryGet(
            name: feed.InstanceName,
            instance: out var destination
        ) &&
            (destination is not null)
        ) {
            feed.EnvelopeRegistration?.Dispose();
            feed.EnvelopeRegistration = destination.Server.Envelope.Configure(
                // The envelope sizes the observer's view from what the candidate's admission discloses this viewer,
                // never the full candidate: what would render nothing measures nothing.
                measure: candidate => ((feed.Observation?.Disclose(candidate: candidate) is { } disclosed)
                    ? emitter.MeasureCandidate(candidate: disclosed)
                    : default)
            );
        }

        return frameSource;
    }
    // A WINDOW session's fit (WorldWindowFrustumFit.FitFrom), asked as its view dresses: the eye is the camera the view
    // of the world the glass stands in renders with in this same frame (the boot world's viewer, the first seat presented
    // in a routed world, or the camera of the window one level up), the apertures that world's face and the counterpart
    // the destination's mirror declares, and the glass that world's row.
    private static Func<CameraSnapshot?> FitWindow(SessionFeed feed) => WorldWindowFrustumFit.FitFrom(
        destination: () => feed.Mirror.Definition,
        eye: feed.Eye,
        local: feed.Local,
        screen: feed.Row
    );
    // Settles every session's and infinity layer's access, then every session's route, for the frame being prepared,
    // before the presenter latches its views and before the view graph host binds them (WorldViewGraphHost.BeginFrame
    // reads AccessOf). A session delivered everything its destination holds renders as a view of the one residency that
    // world already renders through: the boot world's own (the World's residency, which films it after its seats' views,
    // FilmViews), or its endpoint's scene for any other local world (WorldSessionRoute). Any other session renders its
    // own disclosed session.
    private void SettleSessionRoutes() {
        EnsureFeeds();

        foreach (var feed in m_feeds) {
            var access = AccessOf(observation: feed.Observation);

            _ = feed.Route.SettleAccess(access: access, admission: feed.Observation);
            if (feed.Content.Settle(access: access, delivered: feed.Mirror, holdsSource: (feed.FrameSource is not null))) {
                WithdrawOwnSource(feed: feed);
            }
        }
        foreach (var entry in InfinityEntries()) {
            var access = LayerAccessOf(hosted: entry.Hosted);

            _ = entry.Access.Settle(admission: entry.Hosted?.Observation, level: access);
            if (entry.Content.Settle(access: access, delivered: entry.Mirror, holdsSource: (entry.Residency is not null))) {
                WithdrawInfinityResidency(entry: entry);
            }
        }
        if (
            (m_viewPipelines is null) ||
            (Presenter is not { } presenter)
        ) {
            return;
        }

        GroupSharedScenes();
        foreach (var feed in m_feeds) {
            // Only the endpoint of the instance the session observes, and only on the authority's proof.
            var endpoint = (((feed.Observation is { } observation) && m_instanceHost.TryWindowEndpoint(
                endpoint: out var observed,
                name: feed.InstanceName,
                observation: observation
            ))
                ? observed
                : null);
            var home = (
                (endpoint is not null) &&
                string.Equals(
                    a: feed.InstanceName,
                    b: WorldInstanceHost.BootInstanceName,
                    comparisonType: StringComparison.Ordinal
                )
            );

            feed.Home = home;
            feed.Route.Settle(
                endpoint: (home ? null : endpoint),
                presenter: presenter,
                shared: ((endpoint is null) ? SharedSceneOf(feed: feed) : null)
            );
        }
    }
    // The install side of a narrowing (ContentAccess). A session's own frame source and the residency that renders it
    // hold, and their background work (program builds, bakes, probe traces) was submitted over, content delivered under
    // wider access than the viewer has now: the source goes, and the residency with it (ReconcileViewResidencies
    // releases a residency whose source is no longer its feed's), so a job either submitted completes into objects
    // nothing renders.
    private static void WithdrawOwnSource(SessionFeed feed) {
        feed.Emitter?.Dispose();
        feed.Emitter = null;
        feed.EnvelopeRegistration?.Dispose();
        feed.EnvelopeRegistration = null;
        feed.FrameSource = null;
    }
    // An infinity layer's residency, holding content delivered under wider access than the layer has now, goes with
    // every job it submitted.
    private void WithdrawInfinityResidency(InfinityEntry entry) {
        if (entry.Residency is not { } residency) { return; }
        UnregisterViewWork(name: entry.Name);
        residency.Dispose();
        entry.Residency = null;
    }
    // The view a session renders its destination with this frame, as a view of a residency it shares: its camera (a
    // window's fit, or its projection's camera) at a window's or a session screen's quality, under the session's
    // resolution policy. A fitted window's mesh demand measures in the display pixels its glass spans (WindowDemandOf)
    // and holds to its frustum, the aperture from the viewer's eye.
    private SdfViewSnapshot SharedViewOf(SessionFeed feed) {
        var camera = CameraOf(feed: feed, fitted: out var fitted);
        var view = ResolveResolution(
            height: ((uint)(feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight)),
            name: feed.RegistrationName,
            view: new SdfViewSnapshot(
                Camera: camera,
                Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
            ) {
                Quality = (Settings?.NestedViewQuality(view: feed.RegistrationName) ?? default),
                // A window shows the luminance beyond its glass, which the view looking through it exposes.
                Unexposed = (feed.Projection == WorldScreenProjection.Window),
            },
            width: ((uint)(feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth))
        );

        if (!fitted) { return view; }

        return ((WindowDemandOf(fitted: in camera) is > 0f and var shown)
            ? (view with { DemandHeight = shown, DemandWithinView = true })
            : (view with { DemandWithinView = true }));
    }
    // The display pixels a fitted window's glass spans from the presentation's viewer, which its mesh demand measures
    // projected sizes in; zero before a dress has published a viewer, when the view measures in its own image. A window
    // one level down fits to the camera of the window above it, whose rays are the viewer's own, so every level
    // measures by the one viewer.
    private float WindowDemandOf(in CameraSnapshot fitted) => WorldWindowFrustumFit.ShownHeight(
        fitted: in fitted,
        viewerPixelsPerMetre: (m_viewports?.ViewerPixelsPerMetre ?? 0f)
    );
    // Frames every session joined to a routed scene for the frame its residency is about to capture, after the world's
    // own capture has resolved this frame's seat cameras.
    private void FitRoutedWindows(WorldRoutedScene scene) {
        foreach (var feed in m_feeds) {
            if (
                (feed.Route.Window is not { } window) ||
                !ReferenceEquals(
                    objA: window.Scene,
                    objB: scene
                )
            ) {
                continue;
            }

            window.View = SharedViewOf(feed: feed);
        }
    }
    // Captures the world's frame for this frame before a session view dresses its own, so a window's fit reads the seat
    // camera the world renders with in the same frame, whatever order the residencies prepare in. A no-op once the world
    // has captured, or before the first frame is prepared.
    private void CaptureHostFirst() {
        if (
            m_hasFrameContext &&
            (ViewHost is { IsReleased: false } host)
        ) {
            _ = host.HostFrame(context: in m_frameContext);
        }
    }
    // The eye a window of the boot world fits to: the camera the presentation's viewer renders with this frame.
    private Vector3? ViewerEye() => ((m_viewports is { } viewports)
        ? WorldWindowFrustumFit.ViewerEye(viewports: viewports)
        : null);
    // The glass a window of the boot world stands in: its screen's row, read as its view dresses.
    private Func<WorldScreen?> BootRowOf(int screen) => () => RowOf(screen: screen);
}
