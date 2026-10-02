using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One session a world's screen observes its destination through, owned by the authority that declares the
/// screen: the destination it resolved, the mirror its observation feeds, and the observation itself. A presentation
/// reads it (<see cref="WorldInstanceHost.ScreenSession"/>) and renders the mirror; it never admits or ends one.</summary>
public sealed class WorldScreenSession {
    private volatile WorldSessionObservation? m_observation;

    internal WorldScreenSession(string owner, int screenIndex, WorldScreenSource.Session source) {
        Owner = owner;
        ScreenIndex = screenIndex;
        Source = source;
        Mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
    }

    /// <summary>Gets the destination instance's generation id, once resolved.</summary>
    public ulong GenerationId { get; internal set; }
    /// <summary>Gets the destination instance's name, or <see langword="null"/> when the destination did not resolve.</summary>
    public string? InstanceName { get; internal set; }
    /// <summary>Gets a value indicating whether the resolution minted a fresh generation.</summary>
    public bool IsNewGeneration { get; internal set; }
    /// <summary>Gets the mirror the session's observation feeds: what the destination discloses to the screen.</summary>
    public WorldSessionMirror Mirror { get; }
    /// <summary>Gets the live observation, or <see langword="null"/> when the destination refused one.</summary>
    public WorldSessionObservation? Observation {
        get => m_observation;
        internal set => m_observation = value;
    }
    /// <summary>Gets the name of the instance whose screen this is.</summary>
    public string Owner { get; }
    /// <summary>Gets why the screen observes nothing, or <see langword="null"/> while it observes.</summary>
    public string? Refusal { get; internal set; }
    /// <summary>Gets the screen index on the owning world.</summary>
    public int ScreenIndex { get; }
    /// <summary>Gets the authored session source the screen declares, following an edit of its rendering members; an
    /// edit of its destination opens a new session instead.</summary>
    public WorldScreenSource.Session Source { get; internal set; }

    // Set once a destination that ended the session refused another: the screen holds its last image.
    internal bool ReadmissionRefused { get; set; }
    // Whether the last settle forwarded input through this screen, so a stop sends one release.
    internal bool WasForwarding { get; set; }
}
public sealed partial class WorldInstanceHost {
    // Each owning instance's screen sessions by screen index, and the definition they were reconciled against.
    private readonly Dictionary<string, (WorldDefinition? Reconciled, SortedDictionary<int, WorldScreenSession> Rows)> m_screenSessions = new(comparer: StringComparer.Ordinal);
    // The published read view a presentation thread consults: replaced whole whenever a session opens or closes.
    private volatile IReadOnlyDictionary<(string Owner, int Screen), WorldScreenSession> m_publishedScreenSessions = new Dictionary<(string Owner, int Screen), WorldScreenSession>();

    /// <summary>Reads the session a world's screen observes its destination through. Safe from any thread.</summary>
    /// <param name="instanceName">The owning instance.</param>
    /// <param name="screenIndex">The screen index.</param>
    /// <returns>The session, or <see langword="null"/> when the screen declares none or its owner holds none now.</returns>
    public WorldScreenSession? ScreenSession(string instanceName, int screenIndex) => m_publishedScreenSessions.GetValueOrDefault(key: (instanceName, screenIndex));

    // The screens a definition declares that observe a destination: its own screens, then its placement faces.
    private static SortedDictionary<int, WorldScreenSource.Session> DeclaredSessions(WorldDefinition definition) {
        var declared = new SortedDictionary<int, WorldScreenSource.Session>();

        foreach (var screen in definition.Screens) {
            if (screen.Source is WorldScreenSource.Session session) {
                declared[screen.Index] = session;
            }
        }

        foreach (var row in WorldFaceCatalog.For(definition: definition).Rows) {
            if (
                (row.ScreenIndex >= 0) &&
                (row.Source is WorldScreenSource.Session session)
            ) {
                declared[row.ScreenIndex] = session;
            }
        }

        return declared;
    }

    // How many screens deep each running instance is seen, as of the sessions open now (ScreenDepths), and the queue its
    // walk takes; both reused, so a steady step allocates nothing.
    private readonly Dictionary<string, int> m_screenDepths = new(comparer: StringComparer.Ordinal);
    private readonly Queue<string> m_screenDepthQueue = new();

    // Whether an instance has a body a human occupies: someone stands in it.
    private static bool IsOccupied(WorldInstance instance) {
        var population = instance.Server.Population;

        for (var index = 0; (index < population.Capacity); index++) {
            if (population.IsHumanOccupied(bodyIndex: index)) {
                return true;
            }
        }

        return false;
    }

    // The boot world's nesting depth (views.nestingDepth): how many screens deep a presentation shows another world, so
    // how deep the screens it shows observe. The boot world's document decides it, as it decides the presentation's.
    private int NestingDepth => (m_instances.TryGetValue(
        key: BootInstanceName,
        value: out var boot
    )
        ? boot.Server.Definition.Views.NestingDepth
        : RenderGraphInstanceSet.DefaultNestingDepth);

    // Walks the open sessions from every world a presentation shows directly, the boot world and every world a human
    // stands in, at depth 0: a world a session observes is one screen deeper than the world whose screen observes it,
    // at the shallowest such screen.
    private void ScreenDepths() {
        m_screenDepths.Clear();
        m_screenDepthQueue.Clear();

        foreach (var (name, instance) in m_instances) {
            if (
                string.Equals(
                    a: name,
                    b: BootInstanceName,
                    comparisonType: StringComparison.Ordinal
                ) ||
                IsOccupied(instance: instance)
            ) {
                m_screenDepths[name] = 0;
                m_screenDepthQueue.Enqueue(item: name);
            }
        }

        while (m_screenDepthQueue.TryDequeue(result: out var owner)) {
            if (!m_screenSessions.TryGetValue(
                key: owner,
                value: out var owned
            )) {
                continue;
            }

            var depth = (m_screenDepths[owner] + 1);

            foreach (var (_, session) in owned.Rows) {
                if (
                    (session.Observation is { Ended: false }) &&
                    (session.InstanceName is { } observed) &&
                    m_screenDepths.TryAdd(
                        key: observed,
                        value: depth
                    )
                ) {
                    m_screenDepthQueue.Enqueue(item: observed);
                }
            }
        }
    }
    // Whether an instance's screens observe right now: a world a presentation shows directly (the boot world, as a
    // desktop presents it, and any world a human stands in) and every world seen through those worlds' screens, while it
    // is fewer screens deep than the nesting depth. So a portal seen through a portal observes its destination too, to
    // the depth the presentation renders, and a world nobody stands in and nothing within that depth shows boots no
    // destination for its screens. Two portals facing each other end at the depth, since the walk visits each world
    // once, at its shallowest. The depth is document data and sessions are the authority's, so what a world observes
    // never depends on what a presentation draws.
    private bool PresentsScreens(WorldInstance instance) {
        ScreenDepths();

        return (
            m_screenDepths.TryGetValue(
                key: instance.Name,
                value: out var depth
            ) &&
            (depth < NestingDepth)
        );
    }
    // One screen-session line on the host's narration channel.
    private void NarrateScreenSession(string text) {
        if (m_narration.HasNarrationSink) {
            m_narration.Narrate(
                channel: "world.screen",
                text: text
            );
        }
    }
    private void PublishScreenSessions() {
        var published = new Dictionary<(string Owner, int Screen), WorldScreenSession>();

        foreach (var (owner, sessions) in m_screenSessions) {
            foreach (var (screen, session) in sessions.Rows) {
                published[(owner, screen)] = session;
            }
        }

        m_publishedScreenSessions = published;
    }
    // Opens one screen's session: resolves its destination through the observation door, then observes it through the
    // destination's own admission. A refusal that leaves a destination this resolution started with nobody in it stops
    // that instance again.
    private WorldScreenSession OpenScreenSession(WorldInstance owner, int screenIndex, WorldScreenSource.Session source) {
        var session = new WorldScreenSession(
            owner: owner.Name,
            screenIndex: screenIndex,
            source: source
        );

        if (!TryResolveObservedDestination(
            destinationName: source.Destination,
            reason: out var reason,
            resolved: out var resolved,
            source: owner,
            target: out var target
        )) {
            session.Refusal = reason;
            NarrateScreenSession(text: $"[world.screen: {owner.Name} session {screenIndex} refused ({reason})]");

            return session;
        }

        session.GenerationId = resolved.GenerationId;
        session.InstanceName = resolved.InstanceName;
        session.IsNewGeneration = resolved.IsNewGeneration;

        if (target!.Server.TryObserveAsSession(
            refusal: out var refusal,
            sink: session.Mirror,
            sourceAuthority: owner.Server.AuthorityIdentity
        ) is { } observation) {
            session.Observation = observation;
            NarrateScreenSession(text: $"[world.screen: {owner.Name} session {screenIndex} -> destination '{source.Destination}' resolved to instance '{resolved.InstanceName}' generation {resolved.GenerationId}{(resolved.IsNewGeneration
                ? " (new)"
                : "")} as {observation.Session.Describe()} disclosed {observation.Tier}]");

            return session;
        }

        session.Refusal = $"destination '{source.Destination}' refuses a session: {refusal}";
        NarrateScreenSession(text: $"[world.screen: {owner.Name} session {screenIndex} refused ({session.Refusal})]");

        if (
            resolved.IsNewGeneration &&
            ReapIfEmpty(name: resolved.InstanceName)
        ) {
            m_resolver.AbortGeneration(instanceName: resolved.InstanceName);
        }

        return session;
    }
    // Closes one screen's session: ends it, and stops its destination when no other screen observes it and nobody
    // stands in it (a retained destination stays, by ReapIfEmpty's own rule).
    private void CloseScreenSession(WorldScreenSession session) {
        session.Observation?.Dispose();
        session.Observation = null;

        if (session.InstanceName is not { } name) {
            return;
        }

        foreach (var (_, sessions) in m_screenSessions) {
            foreach (var (_, other) in sessions.Rows) {
                if (
                    !ReferenceEquals(
                    objA: other,
                    objB: session
                ) &&
                    string.Equals(
                    a: other.InstanceName,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    return;
                }
            }
        }

        _ = ReapIfEmpty(name: name);
    }
    // Ends every session an instance's screens hold: it stopped presenting, or it retired.
    private void CloseScreenSessions(string owner) {
        if (!m_screenSessions.Remove(
            key: owner,
            value: out var sessions
        )) {
            return;
        }

        foreach (var (_, session) in sessions.Rows) {
            CloseScreenSession(session: session);
        }

        PublishScreenSessions();
    }
    // Maps one forward through the portal onto the destination: the glass point and direction through the door the
    // face's portal names, and the channels by name into the destination's own ordinals.
    private static bool TryMapForward(WorldDefinition source, WorldDefinition destination, in WorldPortalFace face, in WorldPortalForward forward, out PlayerIntent mapped) {
        mapped = default;

        // The counterpart names a declared creation face on a destination placement: its geometry is what the glass maps
        // onto, whether or not that placement authors a face row of its own.
        if (
            !WorldPortalCounterpart.TryParse(
            counterpart: face.Counterpart,
            face: out var counterpartFace,
            placementId: out var counterpartPlacement
        ) ||
            !WorldFaceCatalog.For(definition: destination).TryFind(
            placementId: counterpartPlacement,
            faceName: counterpartFace,
            out var counterpartRow
        )
        ) {
            return false;
        }

        var channels = new ChannelValues();

        for (var ordinal = 0; (ordinal < source.Channels.Count); ordinal++) {
            if (source.Channels[ordinal] is not { } channel) {
                continue;
            }

            for (var target = 0; (target < destination.Channels.Count); target++) {
                if (
                    (destination.Channels[target] is { } named) &&
                    string.Equals(
                    a: named.Name,
                    b: channel.Name,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    channels[target] = forward.Intent[ordinal];

                    break;
                }
            }
        }

        var frame = face.Frame;
        var counterpart = counterpartRow.Frame;

        mapped = new PlayerIntent(
            Channels: channels,
            SourceRay: new SourceRay(
                Direction: WorldFrameIsometry.MapVector(
                    destination: in counterpart,
                    source: in frame,
                    value: forward.Direction
                ),
                Origin: WorldFrameIsometry.MapPoint(
                    destination: in counterpart,
                    point: forward.Hit,
                    source: in frame
                )
            )
        );

        return true;
    }
    // Whether an instance takes the live input it is sent: it steps, rather than holding for a pause, a stop, its
    // mirrors or its retirement, and it is not driving its own tape, whose link drops live input.
    private static bool TakesInput(WorldInstance instance) => (
        (instance.Server.Definition.SimulationRateHz > 0) &&
        !instance.IsPaused &&
        !instance.AwaitingMirrors &&
        !instance.Server.IsRetiring &&
        (instance.Tape is not { Mode: WorldReplayMode.Replaying })
    );
    // Forwards what an instance's engagement routed through its portal faces this step to each face's session, and
    // one release to a face whose forwarding stopped (a disengage, a lost Control hold, a ray off the glass, a replayed
    // tick, or an owner that did not step). Each travels the destination's own link, where its tape records it. A
    // destination that is not taking input (paused, stopped, held, or driving its own tape) is sent nothing, and a
    // release owed to it waits.
    private void ForwardScreenSessions(WorldInstance owner, SortedDictionary<int, WorldScreenSession> sessions, bool stepped) {
        var engagement = owner.Server.Engagement;

        foreach (var (screen, session) in sessions) {
            if (
                (session.Observation is not { Ended: false } observation) ||
                (session.InstanceName is not { } name) ||
                !m_instances.TryGetValue(
                key: name,
                value: out var destination
            )
            ) {
                session.WasForwarding = false;

                continue;
            }

            if (!TakesInput(instance: destination)) {
                continue;
            }

            var mapped = default(PlayerIntent);
            var found = false;

            if (
                stepped &&
                engagement.TryPortalFace(
                face: out var face,
                screenIndex: screen
            )
            ) {
                foreach (var forward in engagement.PortalForwards) {
                    if (
                        (forward.ScreenIndex == screen) &&
                        TryMapForward(
                        destination: destination.Server.Definition,
                        face: in face,
                        forward: in forward,
                        mapped: out mapped,
                        source: owner.Server.Definition
                    )
                    ) {
                        found = true;

                        break;
                    }
                }
            }

            if (
                !found &&
                !session.WasForwarding
            ) {
                continue;
            }

            destination.Link.SubmitIntent(submission: new IntentSubmission(
                EntityIndex: -1,
                Intent: mapped,
                Principal: observation.Session,
                Tick: destination.Server.NextInputTick
            ));
            session.WasForwarding = found;
        }
    }

    /// <summary>Settles one instance's screen sessions after it stepped (or when it was admitted): opens a session for
    /// every screen its live definition declares that observes a destination, closes the sessions of screens it no longer
    /// declares or re-pointed, asks a destination that ended a session to admit it again, and forwards what its
    /// engagement routed through a portal face this step. A world holds sessions only while a presentation shows it
    /// within the nesting depth (the boot world's <c>views.nestingDepth</c>): the boot
    /// world, a world a human stands in, or a world one of their screens shows, to that depth. A replayed tick routes nothing (<see cref="Server.WorldServer.ReplaysInput"/>), since its destinations
    /// are not replaying with it. Called on the thread that steps the instance.</summary>
    /// <param name="instance">The instance that stepped, or that was admitted or held this tick.</param>
    /// <param name="stepped">Whether the instance stepped: false when it was admitted, or held by a pause or a stop,
    /// when its sessions still follow its definition but it forwards nothing.</param>
    public void SettleScreenSessions(WorldInstance instance, bool stepped) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        if (!PresentsScreens(instance: instance)) {
            CloseScreenSessions(owner: instance.Name);

            return;
        }

        if (!m_screenSessions.TryGetValue(
            key: instance.Name,
            value: out var owned
        )) {
            owned = (null, new SortedDictionary<int, WorldScreenSession>());
        }

        var definition = instance.Server.Definition;
        var changed = false;

        if (!ReferenceEquals(
            objA: definition,
            objB: owned.Reconciled
        )) {
            var declared = DeclaredSessions(definition: definition);

            foreach (var screen in owned.Rows.Keys.ToList()) {
                if (
                    !declared.TryGetValue(
                    key: screen,
                    value: out var source
                ) ||
                    !string.Equals(
                    a: source.Destination,
                    b: owned.Rows[screen].Source.Destination,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    CloseScreenSession(session: owned.Rows[screen]);
                    _ = owned.Rows.Remove(key: screen);
                    changed = true;
                } else if (source != owned.Rows[screen].Source) {
                    owned.Rows[screen].Source = source;
                }
            }

            foreach (var (screen, source) in declared) {
                if (!owned.Rows.ContainsKey(key: screen)) {
                    owned.Rows[screen] = OpenScreenSession(
                        owner: instance,
                        screenIndex: screen,
                        source: source
                    );
                    changed = true;
                }
            }

            owned = (definition, owned.Rows);
            m_screenSessions[instance.Name] = owned;
        }

        // A destination that ended a session (a rebuild ends every one) is asked once to admit the screen again,
        // observing into the same mirror; a refusal holds the last image.
        foreach (var (screen, session) in owned.Rows) {
            if (
                (session.Observation is not { Ended: true } ended) ||
                session.ReadmissionRefused ||
                (session.InstanceName is not { } name) ||
                !m_instances.TryGetValue(
                key: name,
                value: out var destination
            )
            ) {
                continue;
            }

            ended.Dispose();

            if (destination.Server.TryObserveAsSession(
                refusal: out var refusal,
                sink: session.Mirror,
                sourceAuthority: instance.Server.AuthorityIdentity
            ) is { } observation) {
                session.Observation = observation;
                NarrateScreenSession(text: $"[world.screen: {instance.Name} session {screen} -> destination '{session.Source.Destination}' ended its session — observing again as {observation.Session.Describe()}]");
            } else {
                session.ReadmissionRefused = true;
                session.Refusal = refusal;
                NarrateScreenSession(text: $"[world.screen: {instance.Name} session {screen} -> destination '{session.Source.Destination}' ended its session and refuses another ({refusal}) — holding last image]");
            }
        }

        ForwardScreenSessions(
            owner: instance,
            sessions: owned.Rows,
            stepped: stepped
        );

        if (changed) {
            PublishScreenSessions();
        }
    }
}
