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
        if (Observations.EnvelopeOf(instance: feed.InstanceName) is { } envelope) {
            feed.EnvelopeRegistration?.Dispose();
            feed.EnvelopeRegistration = envelope.Configure(
                // The envelope sizes the observer's view from what the candidate's admission discloses this viewer,
                // never the full candidate: what would render nothing measures nothing. The authority's capacity check
                // runs it.
                measure: candidate => ((feed.Observation?.Disclose(arg: candidate) is { } disclosed)
                    ? emitter.MeasureCandidate(candidate: disclosed)
                    : default)
            );
        }

        return frameSource;
    }
    // A WINDOW session's fit (WorldWindowFrustumFit.FitFrom), asked as its view dresses: the viewer is the camera the view
    // of the world the glass stands in renders with in this same frame (the boot world's viewer, the first seat presented
    // in a routed world, or the camera of the window one level up), the apertures that world's face and the counterpart
    // the destination's mirror declares, and the glass that world's row.
    private static Func<WorldWindowFit?> FitWindow(SessionFeed feed) => WorldWindowFrustumFit.FitFrom(
        destination: () => feed.Mirror.Definition,
        local: feed.Local,
        screen: feed.Row,
        viewer: feed.Viewer
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

            _ = feed.Route.SettleAccess(access: access, admission: feed.Observation?.Token);
            if (feed.Content.Settle(access: access, delivered: feed.Mirror, holdsSource: (feed.FrameSource is not null))) {
                WithdrawOwnSource(feed: feed);
            }
        }
        foreach (var entry in InfinityEntries()) {
            var access = LayerAccessOf(hosted: entry.Hosted);

            _ = entry.Access.Settle(admission: entry.Hosted?.Observation?.Token, level: access);
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
        m_overlays.Clear();
        m_overlaidMirrors.Clear();
        foreach (var feed in m_feeds) {
            // Only the endpoint of the instance the session observes, and only on the authority's proof: a session
            // handed the whole scene, or all of it but the placements its disclosure varies, which only the boot world's
            // own residency can draw for its audience alone.
            var endpoint = feed.Observation?.Window;
            var boot = ((endpoint is not null) && WorldDefinitionLoader.IsBootInstance(name: feed.InstanceName));
            var whole = (feed.Observation?.Proof is { SharesAuthority: true });
            var audience = ((boot && !whole) ? OverlayAudienceOf(feed: feed, presenter: presenter) : (byte)0);
            var home = (boot && (whole || (audience != 0)));
            var joins = ((whole && !home) ? endpoint : null);

            feed.Home = home;
            feed.Audience = audience;
            feed.Route.Settle(
                endpoint: joins,
                presenter: presenter,
                shared: ((home || (joins is not null)) ? null : SharedSceneOf(feed: feed))
            );
        }
        // An audience no feed shows any more is let go, and is handed to another viewer only once no copy carries it.
        foreach (var mirror in m_audiences.Keys.Where(predicate: mirror => !m_overlaidMirrors.Contains(item: mirror)).ToArray()) { m_audiences.Remove(key: mirror); m_narratedOverlays.Remove(key: mirror); }
        presenter.SetAudienceOverlays(overlays: m_overlays);
    }
    // The view audience each session mirror a home view draws for holds while a feed shows it, the overlays this frame
    // hands the World's scene, and the mirrors they came from.
    private readonly Dictionary<WorldSessionMirror, byte> m_audiences = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<WorldAudienceOverlay> m_overlays = [];
    private readonly HashSet<WorldSessionMirror> m_overlaidMirrors = new(comparer: ReferenceEqualityComparer.Instance);
    private byte m_nextAudience = 1;
    // What the operator was last told of each overlay: the document counted, its varying placements, and whether any copy
    // carries its audience.
    private readonly Dictionary<WorldSessionMirror, (WorldDefinition? Delivered, int Count, bool Carried)> m_narratedOverlays = new(comparer: ReferenceEqualityComparer.Instance);
    // The latest refusal of each document judged for overlays (WorldRigidAssets.AudienceRefusal), by instance, and the
    // refusal last narrated.
    private readonly Puck.Abstractions.CodeDerivedTable<WorldDefinition, AudienceVerdict> m_audienceRefusals = new();
    private string? m_narratedAudienceRefusal;

    // The audience a feed of the boot world, handed all of its scene but the placements its disclosure varies, draws the
    // World's residency for: its mirror's, allocated round the audiences no other mirror holds and no copy still carries,
    // or zero when the World's residency cannot draw its varying placements as copies for it alone (a varying row the
    // catalog does not copy, in the World's document or the session's) or every audience is taken. The feed's overlay
    // (its mirror's document) joins this frame's list.
    private byte OverlayAudienceOf(SessionFeed feed, WorldFramePresenter presenter) {
        var mirror = feed.Mirror;

        if ((BootDefinition() is not { } host) || (mirror.Definition is not { } delivered)) { return 0; }
        var refusal = (AudienceRefusalOf(rows: host, host: host) ?? AudienceRefusalOf(rows: delivered, host: host));

        if (refusal is not null) {
            if (!string.Equals(a: refusal, b: m_narratedAudienceRefusal, comparisonType: StringComparison.Ordinal)) {
                m_narratedAudienceRefusal = refusal;
                Console.Error.WriteLine(value: $"[world.screen: session {feed.ScreenIndex} draws its own scene: the World's residency cannot draw its disclosed placements for it alone ({refusal})]");
            }
            return 0;
        }
        if (!m_audiences.TryGetValue(key: mirror, value: out var audience)) {
            audience = 0;
            for (var tries = 0; (tries < SdfMeshAudience.MaxViewAudience); tries++) {
                var candidate = m_nextAudience;

                m_nextAudience = ((m_nextAudience >= SdfMeshAudience.MaxViewAudience) ? (byte)1 : (byte)(m_nextAudience + 1));
                if (!m_audiences.ContainsValue(value: candidate) && !presenter.CarriesAudience(viewAudience: candidate)) { audience = candidate; break; }
            }
            if (audience == 0) { return 0; }
            m_audiences[mirror] = audience;
        }
        var carried = presenter.CarriesAudience(viewAudience: audience);
        if (!m_narratedOverlays.TryGetValue(key: mirror, value: out var narrated) || !ReferenceEquals(objA: narrated.Delivered, objB: delivered) || (narrated.Carried != carried)) {
            var count = (ReferenceEquals(objA: narrated.Delivered, objB: delivered) ? narrated.Count : VaryingCount(rows: delivered, host: host));

            m_narratedOverlays[mirror] = (delivered, count, carried);
            if ((narrated.Delivered is null) || (narrated.Count != count) || (narrated.Carried != carried)) {
                Console.Error.WriteLine(value: $"[world.screen: session {feed.ScreenIndex} draws the World's residency for audience {audience}, with the {count} placements its disclosure varies as its session is dealt them ({(carried ? "drawn" : "none drawn yet")})]");
            }
        }
        if (m_overlaidMirrors.Add(item: mirror)) { m_overlays.Add(item: new WorldAudienceOverlay(Audience: audience, Definition: delivered)); }
        return audience;
    }
    // A document's refusal for overlays, judged once a document instance and the World's document it copies from.
    private string? AudienceRefusalOf(WorldDefinition rows, WorldDefinition host) {
        return m_audienceRefusals.GetValue(
            derive: static (rows, host) => new AudienceVerdict(Host: host, Refusal: WorldRigidAssets.AudienceRefusal(creations: host, riding: true, rows: rows)),
            holds: static (verdict, _, host) => ReferenceEquals(objA: verdict.Host, objB: host),
            key: rows,
            state: host
        ).Refusal;
    }
    private sealed record AudienceVerdict(WorldDefinition Host, string? Refusal);
    // How many of a delivered document's placements the disclosure varies, for the operator's account of an overlay.
    private static int VaryingCount(WorldDefinition rows, WorldDefinition host) {
        var byId = WorldPlacementIndex.ById(placements: rows.Placements);
        var count = 0;

        foreach (var placement in rows.Placements) {
            if (WorldPlacementAudiences.Varies(definition: host, placement: placement, placementsById: byId)) { count++; }
        }
        return count;
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
        var camera = CameraOf(feed: feed, fit: out var fit);
        var view = ResolveResolution(
            height: ((uint)(feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight)),
            name: feed.RegistrationName,
            view: new SdfViewSnapshot(
                Camera: camera,
                Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
            ) {
                // The disclosure a home view draws the World's residency for; zero for every other feed.
                Audience = feed.Audience,
                Quality = (Settings?.NestedViewQuality(view: feed.RegistrationName) ?? default),
                // A window shows the luminance beyond its glass, which the view looking through it exposes.
                Unexposed = (feed.Projection == WorldScreenProjection.Window),
            },
            width: ((uint)(feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth))
        );

        if (fit is not { Window: var window }) { return view; }

        return ((WindowDemandOf(fitted: in camera) is > 0f and var shown)
            ? (view with { DemandHeight = shown, DemandWithinView = true, ImageWindow = window })
            : (view with { DemandWithinView = true, ImageWindow = window }));
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
            // What the glass shows of its world on the display, which weighs and lights the scene it is a view of.
            window.DisplayShare = (Settings?.NestedDisplayShare(view: feed.RegistrationName) ?? 1f);
            window.IndirectLevels = (Settings?.NestedIndirectLevels(view: feed.RegistrationName) ?? 0);
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
    // The camera a window of the boot world fits to: the one the presentation's viewer renders with this frame
    // (WorldSeatViewports.Viewer), with a seat resolving a view the lowest such seat's, the camera a pointer ray through
    // that view is cast from, so the texel the window shows at a glass point and a click there look along one line.
    private CameraSnapshot? ViewerCamera() => m_viewports?.Viewer;
    // The glass a window of the boot world stands in: its screen's row, read as its view dresses.
    private Func<WorldScreen?> BootRowOf(int screen) => () => RowOf(screen: screen);
}
