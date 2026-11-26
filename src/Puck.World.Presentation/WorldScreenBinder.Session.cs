using Puck.Abstractions.Cameras;
using Puck.SdfVm;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // The session bind: ApplySource's Session arm calls this with the authored session record verbatim, projection and
    // resolution included, at boot and whenever a live document mutation (world.row.set screens/placements) replaces a
    // face's declared source. It resolves and attaches into a local candidate first, so a re-point that fails to
    // resolve leaves the slot's previous feed registered, rendering and holding its lease, and reports the failure by
    // name. Only a live candidate retires the old registration and takes the name; no frame is produced between the
    // release and the register, so a re-point shows no gap.
    private (bool Ok, string Message) ApplySessionSource(int index, ScreenSlot slot, WorldScreenSource.Session session) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
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
    // Called once per produced frame. The authority owns each session screen's session (published at each collect, WorldObservationTable):
    // it opens one when the screen is declared, closes it when the screen is re-pointed or removed, and asks a
    // destination that ended one to admit the screen again. This follows it: a slot whose declared session the host now
    // answers with a different session object (a re-point, or one that was pending when the screen applied) rebinds
    // onto it. A destination instance retiring ends the observation, with a stderr note: the screen's access settles to
    // none, so its picture is withdrawn (SettleSessionRoutes) and it shows nothing until a session observes again.
    private void ReconcileSessionLifecycles() {
        // Indexed, since the rows are an interface list and a foreach over one boxes its enumerator.
        var rows = m_rows;

        for (var at = 0; (at < rows.Count); at++) {
            var row = rows[at];

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
                objA: slot.Session?.Hosted.Token,
                objB: hosted.Token
            )
            ) {
                continue;
            }

            _ = ApplySessionSource(
                index: row.Index,
                session: declared,
                slot: slot
            );
        }

        foreach (var slot in m_slots.Values) {
            if (
                (slot.Session is not { } feed) ||
                feed.InstanceGone ||
                Observations.Runs(instance: feed.InstanceName)
            ) {
                continue;
            }

            feed.InstanceGone = true;
            feed.Route.Access.Close();

            Console.Error.WriteLine(value: $"[world.screen: session {slot.Index} -> destination '{feed.Destination}' instance '{feed.InstanceName}' retired — its picture is withdrawn]");
        }
    }
    // The session the boot world's authority holds for a screen, when it holds one for this very source.
    private WorldObservedSession? HostedSession(int index, WorldScreenSource.Session source) => (((Observations.Screen(
        owner: WorldDefinitionLoader.BootInstanceName,
        screen: index
    ) is { } hosted) && (hosted.Source == source))
        ? hosted
        : null);
    // Completes a resolved session's view registration — deferred from ResolveSession because the render envelope
    // (m_viewPipelines) is not known until the render factory calls ConfigureViews (or a live reconcile runs, by which
    // point it always is). Registration only names the view and fits a window: a session that renders as a view of its
    // destination's one residency (the World's own, or its endpoint scene's) builds nothing more, and one that renders a
    // residency of its own builds its frame source the first time it does (OwnSourceOf).
    private void RegisterSessionView(SessionFeed feed) {
        feed.Registered = true;
        m_accessBindingsVersion++;
        feed.WindowFit = ((feed.Projection == WorldScreenProjection.Window)
            ? FitWindow(feed: feed)
            : null);
    }
    // Releases one slot's view of a session: its envelope and window registrations. The session itself is the
    // authority's, which ends it when the screen is re-pointed or removed; the destination instance
    // leaves the render graph once no slot holds the feed. A nested view passes no reason: the authority narrates the
    // sessions it opens and ends, and a portal crossing would otherwise narrate every view of every level it tears down.
    private void ReleaseSession(SessionFeed feed, int index, string? reason) {
        if (feed.Nested is { } screens) {
            if (m_nestedOwners.Remove(key: screens)) {
                m_accessBindingsVersion++;
            }
            screens.Close(sessions: NestedSessionsOf());
            feed.Nested = null;
        }

        feed.Route.Access.Close();
        feed.Dispose();
        m_feedsMoved = true;

        if (reason is not null) {
            Console.Error.WriteLine(value: $"[world.screen: session {index} -> destination '{feed.Destination}' released ({reason})]");
        }
    }
    // The registered session feed a view instance name names, at any depth, or null.
    private SessionFeed? SessionFeedOf(string name) {
        EnsureFeeds();

        return ((m_feedsByName.TryGetValue(
            key: name,
            value: out var feed
        ) && feed.Registered)
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
            current: Current,
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
            Viewer = ViewerCamera,
            Local = BootDefinition,
            Row = BootRowOf(screen: index),
        };
    }
    // The boot world's definition, as the client was delivered it.
    private WorldDefinition? BootDefinition() => m_client.Definition;
    // The frame's shared scenes: each feed whose authority proved its stream is handed the same scene inputs as
    // another feed's of the same world, by the first such feed in feed order (its lead, whose mirror the scene
    // draws), and each group's first feed by its world and scene identity. A feed alone in its group shares nothing.
    private readonly Dictionary<SessionFeed, SessionFeed> m_sharedLeads = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string Instance, Puck.Assets.ContentPin Scene), (SessionFeed Lead, int Count)> m_sharedGroups = [];

    // Groups the feeds by the scene their authority proved each is handed, for the frame being prepared.
    private void GroupSharedScenes() {
        m_sharedLeads.Clear();
        m_sharedGroups.Clear();
        foreach (var feed in m_feeds) {
            if (SharedProofOf(feed: feed) is not { } proof) { continue; }
            var key = (feed.InstanceName, proof.Scene);
            var group = m_sharedGroups.GetValueOrDefault(key: key, defaultValue: (Lead: feed, Count: 0));
            m_sharedGroups[key] = (group.Lead, (group.Count + 1));
        }
        foreach (var feed in m_feeds) {
            if (
                (SharedProofOf(feed: feed) is { } proof) &&
                m_sharedGroups.TryGetValue(key: (feed.InstanceName, proof.Scene), value: out var group) &&
                (group.Count > 1)
            ) {
                m_sharedLeads[feed] = group.Lead;
            }
        }
    }
    // The authority's current proof about a feed's stream, while the instance that issued it is the one the feed's
    // session observes.
    // The collect checked the proof against the instance running under the name (ProofHolds).
    private WorldSceneShare? SharedProofOf(SessionFeed feed) => (
        !feed.InstanceGone &&
        (feed.Observation is { Proof: { } proof, ProofHolds: true })
            ? proof
            : null);
    // The scene a feed shares this frame: its group's, drawn from its lead's mirror, on a frame whose latched delivery
    // both the feed's proof and its lead's cover, so the scene the frame binds was handed to both streams alike. None
    // while the feed is alone in its group or the latched delivery falls outside either proof.
    private WorldSharedScene? SharedSceneOf(SessionFeed feed) {
        if (!m_sharedLeads.TryGetValue(key: feed, value: out var lead)) { return null; }
        var tick = lead.Mirror.Tick;
        if (!(SharedProofOf(feed: feed)?.Covers(tick: tick) ?? false) || !(SharedProofOf(feed: lead)?.Covers(tick: tick) ?? false)) { return null; }
        return new WorldSharedScene(Authority: feed.InstanceName, Mirror: lead.Mirror, Name: lead.SharedName,
            Presented: string.Equals(a: feed.InstanceName, b: WorldDefinitionLoader.BootInstanceName, comparisonType: StringComparison.Ordinal));
    }
    // The routed window a session view renders through this frame, once the presenter's latch includes it.
    private WorldRoutedWindow? RoutedWindowOf(SessionFeed feed) => (
        ((feed.Route.Window is { Index: >= 0 } window) &&
        (Presenter is { } presenter) &&
        presenter.Presents(scene: window.Scene))
            ? window
            : null);
    // The screen row ReconcileScreens last applied for an index, or null.
    private WorldScreen? RowOf(int screen) => RowOf(
        rows: m_rows,
        screen: screen
    );
    // The row a screen list declares for an index, or null.
    private static WorldScreen? RowOf(IReadOnlyList<WorldScreen> rows, int screen) {
        for (var index = 0; (index < rows.Count); index++) {
            if (rows[index].Index == screen) {
                return rows[index];
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
    /// <param name="InstanceGone">Whether the destination instance retired, which ended the observation and withdrew the
    /// screen's picture.</param>
    /// <param name="Projection">How the destination render projects onto this face — see
    /// <see cref="WorldScreenProjection"/>.</param>
    /// <param name="RenderWidth">The resolved offscreen render width, pixels — the true cost this session pays every
    /// time it produces a frame.</param>
    /// <param name="RenderHeight">The resolved offscreen render height, pixels.</param>
    /// <param name="RendersEveryFrame">Whether this session renders on every produced frame that shows its glass — a
    /// window does, paying its full render cost each such frame, where an ordinary camera projection refreshes at the
    /// views' divisor (<c>world.view-refresh</c>). A glass out of every view's sight, or too small in it, renders on
    /// no frame (<see cref="WorldViewGraphHost"/>).</param>
    public readonly record struct WorldSessionDescription(string Destination, string? RequestedCamera, string? EffectiveCamera, string InstanceName, ulong GenerationId, Principal Session, WorldDisclosureTier Tier, bool LeaseHeld, bool InstanceGone, WorldScreenProjection Projection, int RenderWidth, int RenderHeight, bool RendersEveryFrame);

    // One session-sourced screen's live state: which destination it observes, its resolved instance/generation, the
    // attached observation lease + client-side mirror, and (once GPU services are configured) its registered
    // offscreen view. A mutable class so a lifecycle transition (re-point, teardown, instance-retired) updates it in
    // place; the constructor parameters are immutable facts about ONE resolution (a re-point builds a fresh instance
    // rather than mutating this one — see ApplySessionSource).
    private sealed partial class SessionFeed(int depth, string destination, string? requestedCamera, string? effectiveCamera, string instanceName, ulong generationId, WorldObservedSession hosted, string registrationName, WorldScreenProjection projection, WorldScreenResolution? resolution, int screenIndex, Func<WorldObservedSession?, WorldObservedSession?> current) : IDisposable {
        // How many screens deep the destination is seen: 1 for a boot or routed world's own screen, one more a level.
        public int Depth { get; } = depth;
        public string Destination { get; } = destination;

        // The view whose world the screen stands in, or null for a screen of a world the display shows directly.
        public SessionFeed? ParentFeed { get; init; }
        // The infinity scene whose physical screen this is, when it has no parent session feed.
        public InfinityEntry? ParentInfinity { get; init; }
        // The routed world whose own screen this is, or null.
        public WorldRoutedScene? RootScene { get; init; }

        // The camera a window fits to, the world its glass stands in, and its glass's row, each read as the view dresses.
        public Func<CameraSnapshot?> Viewer { get; init; } = static () => null;
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
        // The authority's session this feed renders, as the last collect published it: its mirror, and the observation
        // it currently holds.
        public WorldObservedSession Hosted => (current(arg: hosted) ?? hosted);

        public WorldSessionMirror Mirror => Hosted.Mirror;

        public string RegistrationName { get; } = registrationName;
        // The name of the scene this feed leads for the feeds that share it (WorldViewNames.Shared), read every frame.
        public string SharedName => (m_sharedName ??= WorldViewNames.Shared(lead: RegistrationName));

        private string? m_sharedName;
        public WorldScreenProjection Projection { get; } = projection;
        public WorldScreenResolution? Resolution { get; } = resolution;

        // The session the screen observes as, replaced by the authority when the destination ends it and admits
        // another; null once the destination refused another.
        public WorldObservationAdmission? Observation => Hosted.Observation;

        // The feed's access and route into its destination's endpoint scene and whether it renders as a view of the
        // World's own residency instead (its destination is the boot world), all settled every frame
        // (SettleSessionRoutes), the access first as the feed is made, and a window's fit (WorldScreenBinder.FitWindow),
        // which every view of it frames with.
        public WorldSessionRoute Route { get; } = SettledRoute(observation: hosted.Observation);
        // What the session's own residency may render (ContentAccess), settled with its access.
        public ContentAccess Content { get; } = new(level: AccessOf(observation: hosted.Observation), mirror: hosted.Mirror);
        public bool Home { get; set; }
        // The audience a home view of the feed draws the World's residency for (SdfMeshAudience): zero while the session
        // is handed the whole scene, an overlay's while it is handed all but the placements its disclosure varies.
        public byte Audience { get; set; }

        public Func<WorldWindowFit?>? WindowFit { get; set; }
        // Whether the views are configured for the feed (RegisterSessionView).
        public bool Registered { get; set; }
        // The emitter and frame source a residency of the feed's own renders, built the first time it needs one
        // (OwnSourceOf), and the destination's envelope they size.
        public WorldSessionSceneEmitter? Emitter { get; set; }
        public IDisposable? EnvelopeRegistration { get; set; }
        public SdfCompositionFrameSource? FrameSource { get; set; }
        // Set by ReconcileSessionLifecycles the moment the resolved instance stops running.
        public bool InstanceGone { get; set; }

        // Releases the envelope and window registrations; the session is the authority's, and the session's instance
        // leaves the render graph once no slot holds the feed.
        public void Dispose() {
            Emitter?.Dispose();
            FrameSource?.Dispose();
            Route.Dispose();
            EnvelopeRegistration?.Dispose();
            EnvelopeRegistration = null;
        }

    }
}
