using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // The session bind: ApplySource's Session arm calls this with the authored session record verbatim, projection and
    // resolution included, at boot and whenever a live document mutation (world.row.set screens/placements) replaces a
    // face's declared source. It resolves and attaches into a local candidate first, so a re-point that fails to
    // resolve leaves the slot's previous feed registered, rendering and holding its lease, and reports the failure by
    // name. Only a live candidate retires the old registration and takes the name; no frame is produced between the
    // release and the register, so a re-point shows no gap.
    private (bool Ok, string Message) ApplySessionSource(int index, WorldScreenSource.Session session) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        var previous = slot.Session;
        var feed = ResolveSession(
            session: session,
            slot: slot
        );

        if (feed is null) {
            return (Ok: false, Message: (slot.DeclaredFault ?? $"screen {index} session bind failed"));
        }

        if (previous is { } oldFeed) {
            ReleaseSession(
                feed: oldFeed,
                index: index,
                reason: "source re-pointed"
            );
        }

        slot.Session = feed;
        m_feedsMoved = true;

        if (m_viewPipelines is not null) {
            RegisterSessionView(feed: feed);
        }

        ReconcileViews();

        return (Ok: true, Message: $"screen {index} showing session '{session.Destination}' -> instance '{feed.InstanceName}'");
    }
    // Called once per produced frame. The authority owns each session screen's session (WorldInstanceHost.ScreenSession):
    // it opens one when the screen is declared, closes it when the screen is re-pointed or removed, and asks a
    // destination that ended one to admit the screen again. This follows it: a slot whose declared session the host now
    // answers with a different session object (a re-point, or one that was pending when the screen applied) rebinds
    // onto it. A destination instance retiring drops the projection to a held last image with a stderr note: the mirror
    // simply stops receiving deliveries and Resolve keeps re-rendering its last mirrored definition.
    private void ReconcileSessionLifecycles() {
        foreach (var row in m_rows) {
            if (
                (row.Source is not WorldScreenSource.Session declared) ||
                !m_slots.TryGetValue(
                key: row.Index,
                value: out var slot
            ) ||
                (HostedSession(
                index: row.Index,
                source: declared
            ) is not { } hosted) ||
                ReferenceEquals(
                objA: slot.Session?.Hosted,
                objB: hosted
            )
            ) {
                continue;
            }

            _ = ApplySessionSource(
                index: row.Index,
                session: declared
            );
        }

        foreach (var slot in m_slots.Values) {
            if (
                (slot.Session is not { } feed) ||
                feed.InstanceGone ||
                (m_instanceHost.TryGet(
                name: feed.InstanceName,
                instance: out var destination
            ) && (destination is not null))
            ) {
                continue;
            }

            feed.InstanceGone = true;

            Console.Error.WriteLine(value: $"[world.screen: session {slot.Index} -> destination '{feed.Destination}' instance '{feed.InstanceName}' retired — holding last image]");
        }
    }
    // The session the boot world's authority holds for a screen, when it holds one for this very source.
    private WorldScreenSession? HostedSession(int index, WorldScreenSource.Session source) => (((m_instanceHost.ScreenSession(
        instanceName: WorldInstanceHost.BootInstanceName,
        screenIndex: index
    ) is { } hosted) && (hosted.Source == source))
        ? hosted
        : null);
    // Completes a resolved session's view registration — deferred from ResolveSession because the render envelope
    // (m_viewPipelines) is not known until the render factory calls ConfigureViews (or a live reconcile runs, by which
    // point it always is): one WorldSessionSceneEmitter composed through its own SdfCompositionFrameSource, which the
    // session's instance renders under the slot's own name — NOT shared across screens even when two name the same
    // destination and camera, since the shipped content never needs that and a shared view would complicate the
    // per-slot teardown.
    private void RegisterSessionView(SessionFeed feed) {
        var emitter = new WorldSessionSceneEmitter(
            mirror: feed.Mirror,
            effectiveCameraName: feed.EffectiveCamera
        );
        var frameSource = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );
        var isWindow = (feed.Projection == WorldScreenProjection.Window);
        var width = (feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth);
        var height = (feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight);

        feed.FrameSource = frameSource;
        feed.Emitter = emitter;
        feed.WindowFit = (isWindow
            ? FitWindow(feed: feed)
            : null);
        emitter.SetWindowFit(fit: feed.WindowFit);

        if (isWindow) {
            feed.SetWindowLease(lease: WorldSessionWindowLeases.Acquire(
                height: height,
                width: width
            ));
        }

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
                allowGrowth: true,
                programWordCapacity: frameSource.WorstCaseProgramWordCapacity,
                instanceCapacity: frameSource.WorstCaseInstanceCapacity,
                // The envelope sizes the observer's view from what the candidate's admission discloses this viewer,
                // never the full candidate: what would render nothing measures nothing.
                measure: candidate => ((feed.Observation?.Disclose(candidate: candidate) is { } disclosed)
                    ? emitter.MeasureCandidate(candidate: disclosed)
                    : (Words: 0, Instances: 0))
            );
        }
    }
    // Releases one slot's view of a session: its envelope and window registrations. The session itself is the
    // authority's (WorldInstanceHost), which ends it when the screen is re-pointed or removed; the destination instance
    // leaves the render graph once no slot holds the feed.
    private void ReleaseSession(SessionFeed feed, int index, string reason) {
        if (feed.Nested is { } screens) {
            _ = m_nestedOwners.Remove(key: screens);
            screens.Close(sessions: NestedSessionsOf());
            feed.Nested = null;
        }

        feed.Dispose();
        m_feedsMoved = true;

        Console.Error.WriteLine(value: $"[world.screen: session {index} -> destination '{feed.Destination}' released ({reason})]");
    }
    // The registered session feed a view instance name names, at any depth, or null.
    private SessionFeed? SessionFeedOf(string name) {
        EnsureFeeds();

        return ((m_feedsByName.TryGetValue(
            key: name,
            value: out var feed
        ) && (feed.FrameSource is not null))
            ? feed
            : null);
    }
    // Drops a slot's session reference and releases its registration/lease — the symmetric half of
    // ApplySessionSource's acquire, run whenever the slot stops observing that destination (a source change away from
    // Session, or a screen removal).
    private void ReleaseSlotSession(ScreenSlot slot) {
        if (slot.Session is not { } feed) {
            return;
        }

        slot.Session = null;
        ReleaseSession(
            feed: feed,
            index: slot.Index,
            reason: "source changed"
        );
    }
    // Refuses an unknown camera at bind time with a loud stderr note and falls back to the default projection,
    // never a boot refusal. An absent request resolves to null (the default projection) with no narration — that is
    // ordinary, not a fault.
    private static string? ResolveEffectiveCameraName(WorldDefinition destinationDefinition, string? requested, int index, string destinationName) {
        if (requested is not { } name) {
            return null;
        }

        foreach (var camera in destinationDefinition.Cameras) {
            if (string.Equals(
                a: camera.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return name;
            }
        }

        Console.Error.WriteLine(value: $"[world.screen: session {index} -> destination '{destinationName}' names unknown camera '{name}' — falling back to the default projection]");

        return null;
    }
    // Binds a slot to the session the boot world's authority holds for its declared source — the boot loop and
    // ApplySessionSource both call this. Returns the new feed on success (slot.DeclaredFault cleared); returns null
    // when the authority holds none for this source yet (a live re-point the host settles at its next step, which
    // ReconcileSessionLifecycles then binds) or when the destination refused it (slot.DeclaredFault names why).
    // Deliberately never touches slot.Session itself: a re-point must keep the slot's previous feed until a new one
    // exists. GPU view registration is a separate step (RegisterSessionView).
    private SessionFeed? ResolveSession(ScreenSlot slot, WorldScreenSource.Session session) {
        if (HostedSession(
            index: slot.Index,
            source: session
        ) is not { } hosted) {
            slot.DeclaredFault = $"the session to destination '{session.Destination}' awaits its authority";

            return null;
        }

        if (
            (hosted.Observation is null) ||
            (hosted.InstanceName is not { } instanceName)
        ) {
            slot.DeclaredFault = (hosted.Refusal ?? $"destination '{session.Destination}' is not observed");

            Console.Error.WriteLine(value: $"[world.screen: session {slot.Index} refused ({slot.DeclaredFault})]");

            return null;
        }

        var effectiveCamera = ResolveEffectiveCameraName(
            destinationDefinition: hosted.Mirror.Definition,
            requested: session.CameraName,
            index: slot.Index,
            destinationName: session.Destination
        );

        slot.DeclaredFault = null;

        var index = slot.Index;

        return new SessionFeed(
            depth: 1,
            destination: session.Destination,
            effectiveCamera: effectiveCamera,
            generationId: hosted.GenerationId,
            hosted: hosted,
            instanceName: instanceName,
            projection: session.Projection,
            registrationName: WorldViewNames.Session(screen: index),
            requestedCamera: session.CameraName,
            resolution: session.Resolution,
            screenIndex: index
        ) {
            Eye = () => ((m_viewports is { } viewports)
                ? WorldWindowFrustumFit.ViewerEye(viewports: viewports)
                : null),
            Local = BootDefinition,
            Row = () => RowOf(screen: index),
        };
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
    // The boot world's definition, as its authority holds it.
    private WorldDefinition? BootDefinition() => (m_instanceHost.TryGet(
        instance: out var boot,
        name: WorldInstanceHost.BootInstanceName
    )
        ? boot?.Server.Definition
        : null);
    // Settles every WINDOW session's route for the frame being prepared, before the presenter latches its views: a
    // session delivered everything its destination holds joins that destination's endpoint scene, any other renders its
    // own disclosed session (WorldSessionWindowRoute).
    private void SettleWindowRoutes() {
        if (
            (m_viewPipelines is null) ||
            (Presenter is not { } presenter)
        ) {
            return;
        }

        EnsureFeeds();

        foreach (var feed in m_feeds) {
            if (feed.Projection != WorldScreenProjection.Window) {
                continue;
            }

            // Only the endpoint of the instance the session observes, and only while it discloses everything.
            var endpoint = (((feed.Observation is { } observation) && m_instanceHost.TryWindowEndpoint(
                endpoint: out var observed,
                name: feed.InstanceName,
                observation: observation
            ))
                ? observed
                : null);

            feed.WindowRoute.Settle(
                disclosesEverything: (endpoint is not null),
                endpoint: endpoint,
                presenter: presenter
            );
        }
    }
    // Frames every window joined to a routed scene for the frame its residency is about to capture, after the world's
    // own capture has resolved this frame's seat cameras: its fit at a session screen's quality, or the scene's default
    // projection while the fit has no answer.
    private void FitRoutedWindows(WorldRoutedScene scene) {
        foreach (var feed in m_feeds) {
            if (
                (feed.WindowRoute.Window is not { } window) ||
                !ReferenceEquals(
                    objA: window.Scene,
                    objB: scene
                )
            ) {
                continue;
            }

            window.View = ((feed.WindowFit?.Invoke() is { } camera)
                ? new SdfViewSnapshot(
                    Camera: camera,
                    Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
                ) {
                    Quality = WorldSessionSceneEmitter.ReducedQuality,
                }
                : null);
        }
    }
    // The routed window a session view renders through this frame, once the presenter's latch includes it.
    private WorldRoutedWindow? RoutedWindowOf(SessionFeed feed) => (
        ((feed.WindowRoute.Window is { Index: >= 0 } window) &&
        (Presenter is { } presenter) &&
        presenter.Presents(scene: window.Scene))
            ? window
            : null);
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
    // The screen row ReconcileScreens last applied for an index, or null.
    private WorldScreen? RowOf(int screen) {
        for (var index = 0; (index < m_rows.Count); index++) {
            if (m_rows[index].Index == screen) {
                return m_rows[index];
            }
        }

        return null;
    }

    /// <summary>Reads back a screen index's session projection state, when it carries one.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="description">The session's live state, on success.</param>
    /// <returns>Whether the index carries a resolved session projection.</returns>
    public bool TryDescribeSession(int index, out WorldSessionDescription description) {
        if (
            m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) &&
            (slot.Session is { } feed)
        ) {
            var isWindow = (feed.Projection == WorldScreenProjection.Window);

            description = new WorldSessionDescription(
                Destination: feed.Destination,
                RequestedCamera: feed.RequestedCamera,
                EffectiveCamera: feed.EffectiveCamera,
                InstanceName: feed.InstanceName,
                GenerationId: feed.GenerationId,
                Session: (feed.Observation?.Session ?? default),
                Tier: (feed.Observation?.Tier ?? WorldDisclosureTier.Frames),
                LeaseHeld: !feed.InstanceGone,
                InstanceGone: feed.InstanceGone,
                Projection: feed.Projection,
                RenderWidth: (feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth),
                RenderHeight: (feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight),
                RendersEveryFrame: isWindow
            );

            return true;
        }

        description = default;

        return false;
    }

    /// <summary>One session-sourced screen's live projection state — the <c>world.faces</c> read-back's session
    /// extension.</summary>
    /// <param name="Destination">The observed destination row's name.</param>
    /// <param name="RequestedCamera">The authored camera name, or <see langword="null"/> for the default projection.</param>
    /// <param name="EffectiveCamera">The camera actually rendered through — <see langword="null"/> when the
    /// requested camera was absent/unknown at bind time (or none was authored) and the default projection applies.</param>
    /// <param name="InstanceName">The resolved destination instance's process-local name.</param>
    /// <param name="GenerationId">The resolved generation id — the same id a crossing at the same door would land in.</param>
    /// <param name="Session">The session principal the screen observes as.</param>
    /// <param name="Tier">What the destination's admission discloses to the screen.</param>
    /// <param name="LeaseHeld">Whether the observation lease's destination instance is still running.</param>
    /// <param name="InstanceGone">Whether the destination instance retired — the projection holds its last image.</param>
    /// <param name="Projection">How the destination render projects onto this face — see
    /// <see cref="WorldScreenProjection"/>.</param>
    /// <param name="RenderWidth">The resolved offscreen render width, pixels — the true cost this session pays every
    /// time it produces a frame (see <see cref="WorldSessionWindowLeases"/> for a window's own accounting).</param>
    /// <param name="RenderHeight">The resolved offscreen render height, pixels.</param>
    /// <param name="RendersEveryFrame">Whether this session renders on every produced frame — a window always does,
    /// paying its full render cost each frame, where an ordinary camera projection refreshes at the views' divisor
    /// (<c>world.view-refresh</c>).</param>
    internal readonly record struct WorldSessionDescription(string Destination, string? RequestedCamera, string? EffectiveCamera, string InstanceName, ulong GenerationId, Principal Session, WorldDisclosureTier Tier, bool LeaseHeld, bool InstanceGone, WorldScreenProjection Projection, int RenderWidth, int RenderHeight, bool RendersEveryFrame);

    // One session-sourced screen's live state: which destination it observes, its resolved instance/generation, the
    // attached observation lease + client-side mirror, and (once GPU services are configured) its registered
    // offscreen view. A mutable class so a lifecycle transition (re-point, teardown, instance-retired) updates it in
    // place; the constructor parameters are immutable facts about ONE resolution (a re-point builds a fresh instance
    // rather than mutating this one — see ApplySessionSource).
    private sealed class SessionFeed(int depth, string destination, string? requestedCamera, string? effectiveCamera, string instanceName, ulong generationId, WorldScreenSession hosted, string registrationName, WorldScreenProjection projection, WorldScreenResolution? resolution, int screenIndex) : IDisposable {
        // How many screens deep the destination is seen: 1 for a boot or routed world's own screen, one more a level.
        public int Depth { get; } = depth;
        public string Destination { get; } = destination;

        // The view whose world the screen stands in, or null for a screen of a world the display shows directly.
        public SessionFeed? ParentFeed { get; init; }
        // The routed world whose own screen this is, or null.
        public WorldRoutedScene? RootScene { get; init; }

        // The eye a window fits to, the world its glass stands in, and its glass's row, each read as the view dresses.
        public Func<Vector3?> Eye { get; init; } = static () => null;
        public Func<WorldDefinition?> Local { get; init; } = static () => null;
        public Func<WorldScreen?> Row { get; init; } = static () => null;

        // The destination's own screens as this view shows them, one level deeper.
        public WorldNestedScreens<SessionFeed>? Nested { get; set; }

        // The screen the session shows on.
        public int ScreenIndex { get; } = screenIndex;
        public string? RequestedCamera { get; } = requestedCamera;
        public string? EffectiveCamera { get; } = effectiveCamera;
        public string InstanceName { get; } = instanceName;
        public ulong GenerationId { get; } = generationId;
        // The authority's session this feed renders: its mirror, and the observation it currently holds.
        public WorldScreenSession Hosted { get; } = hosted;

        public WorldSessionMirror Mirror => Hosted.Mirror;

        public string RegistrationName { get; } = registrationName;
        public WorldScreenProjection Projection { get; } = projection;
        public WorldScreenResolution? Resolution { get; } = resolution;

        // The session the screen observes as, replaced by the authority when the destination ends it and admits
        // another; null once the destination refused another.
        public WorldSessionObservation? Observation => Hosted.Observation;

        // Acquired only for a WINDOW projection (WorldSessionWindowLeases) — the runtime accounting world.faces'
        // true-cost echo reads; the DOCUMENT-level refusal is WorldDefinitionValidator's, at boot/mutation time, not
        // this lease.
        private IDisposable? WindowLease { get; set; }

        // A WINDOW feed's route into its destination's endpoint scene, settled every frame (SettleWindowRoutes), and its
        // fit (WorldScreenBinder.FitWindow), which its own emitter and its routed view both frame with.
        public WorldSessionWindowRoute WindowRoute { get; } = new();

        public Func<CameraSnapshot?>? WindowFit { get; set; }
        // Set by RegisterSessionView (its own constructed instance).
        public WorldSessionSceneEmitter? Emitter { get; set; }
        public IDisposable? EnvelopeRegistration { get; set; }
        // The frame source the session's instance renders, set once the views are configured (RegisterSessionView).
        public SdfCompositionFrameSource? FrameSource { get; set; }
        // Set by ReconcileSessionLifecycles the moment the resolved instance stops running — the projection then
        // holds its last mirrored image (the instance keeps rendering the mirror's frozen definition; nothing here
        // needs to force that, since the mirror simply stops receiving deliveries).
        public bool InstanceGone { get; set; }

        // Releases the envelope and window registrations; the session is the authority's, and the session's instance
        // leaves the render graph once no slot holds the feed.
        public void Dispose() {
            WindowRoute.Dispose();
            EnvelopeRegistration?.Dispose();
            EnvelopeRegistration = null;
            WindowLease?.Dispose();
            WindowLease = null;
        }        /// <summary>Acquires (replacing any prior) this feed's window-cost lease.</summary>
        public void SetWindowLease(IDisposable lease) {
            WindowLease?.Dispose();
            WindowLease = lease;
        }
    }
}
