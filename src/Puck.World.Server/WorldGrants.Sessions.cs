using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    // The live unembodied sessions this world admitted, by ordinal, and the epoch each ordinal last carried. The epochs
    // outlive the sessions, so a reused ordinal's next session carries a later epoch and a principal naming a retired
    // one never matches a live session again. A checkpoint carries each session as it stands (CaptureSessions); the
    // screen observing through it is not state of this world, so a restore brings the session back unobserved and a
    // rebuild ends every one (EndSessionsForRebuild).
    private readonly Dictionary<int, SessionEntry> m_sessions = new();
    private readonly Dictionary<int, int> m_sessionEpochs = new();

    // One past the highest ordinal a session ever took here: the bound a read over every live session walks.
    private int m_sessionOrdinalBound;

    /// <summary>Allocates a local input producer's session identity without minting capabilities. Its input owner
    /// records the actual actor, whose current grants are checked at every submission.</summary>
    internal Principal AdmitInputControllerSession() {
        var ordinal = 0;
        while (m_sessions.ContainsKey(ordinal)) { ordinal++; }
        var session = Principal.Session(ordinal, checked(m_sessionEpochs.GetValueOrDefault(ordinal) + 1));
        // Derived from an already recorded producer/command, so replay derives this admission again as well.
        ApplySessionAdmitted(new(session, [], []));
        return session;
    }

    // The verdict's rows that name a subject: the grants a session holds with no body.
    private static List<WorldGrant> BuildSessionGrants(Principal session, IReadOnlyList<WorldAdmissionGrant> templates) {
        var minted = new List<WorldGrant>(capacity: templates.Count);

        foreach (var template in templates) {
            if (template.Subject is not { } subject) {
                continue;
            }

            minted.Add(item: new WorldGrant(
                Grantee: session,
                Capability: template.Capability,
                Subject: subject,
                Exclusive: template.Exclusive,
                Budget: template.Budget,
                EventBudget: template.EventBudget,
                KindMask: template.KindMask
            ));
        }

        return minted;
    }
    // The verdict's body-relative rows, minted over the body an embodiment assigns.
    private static List<WorldGrant> BuildEmbodimentGrants(Principal session, int bodyIndex, IReadOnlyList<WorldAdmissionGrant> templates) {
        var minted = new List<WorldGrant>(capacity: templates.Count);

        foreach (var template in templates) {
            if (template.Subject is not null) {
                continue;
            }

            minted.Add(item: new WorldGrant(
                Grantee: session,
                Capability: template.Capability,
                Subject: template.SubjectFor(bodyIndex: bodyIndex),
                Exclusive: template.Exclusive,
                Budget: template.Budget,
                EventBudget: template.EventBudget,
                KindMask: template.KindMask
            ));
        }

        return minted;
    }
    // Every admitted session as it stands, in ordinal order: what Restore re-admits.
    private WorldSessionCheckpoint[] CaptureSessions() {
        var sessions = new List<WorldSessionCheckpoint>(capacity: m_sessions.Count);

        foreach (var entry in m_sessions.OrderBy(keySelector: static pair => pair.Key).Select(selector: static pair => pair.Value)) {
            sessions.Add(item: new WorldSessionCheckpoint(
                BodyGeneration: entry.BodyGeneration,
                BodyIndex: entry.BodyIndex,
                Input: entry.Input,
                ObserverEnded: entry.ObserverEnded,
                Session: entry.Principal,
                Templates: entry.Templates
            ));
        }

        return [.. sessions];
    }
    // Replaces the session table with a checkpoint's: each session under its own principal, with the body it was
    // embodied in and the input its viewer last forwarded. Its rows were restored with every other grantee's.
    private void RestoreSessions(IReadOnlyList<WorldSessionCheckpoint> sessions) {
        m_sessions.Clear();

        foreach (var session in sessions) {
            if (session.Session.Kind != PrincipalKind.Session) {
                throw new InvalidOperationException(message: $"the checkpoint's session table names {session.Session.Describe()}, which is not a session principal");
            }
            if (!m_sessions.TryAdd(
                key: session.Session.Index,
                value: new SessionEntry(
                    principal: session.Session,
                    templates: session.Templates
                ) {
                    BodyGeneration = session.BodyGeneration,
                    BodyIndex = session.BodyIndex,
                    Input = session.Input,
                    ObserverEnded = session.ObserverEnded,
                }
            )) {
                throw new InvalidOperationException(message: $"the checkpoint's session table holds ordinal {session.Session.Index} twice");
            }

            m_sessionEpochs[session.Session.Index] = Math.Max(
                val1: session.Session.Generation,
                val2: m_sessionEpochs.GetValueOrDefault(key: session.Session.Index)
            );
            m_sessionOrdinalBound = Math.Max(
                val1: m_sessionOrdinalBound,
                val2: (session.Session.Index + 1)
            );
        }
    }

    /// <summary>Determines whether a live session holds <c>observe all</c>, its whole-world view: what its observation
    /// is delivered while it does.</summary>
    /// <param name="session">The session principal.</param>
    /// <returns><see langword="true"/> when the session observes.</returns>
    public bool ObservesAsSession(Principal session) => (
        IsLiveSession(principal: session) &&
        Allows(
            capability: WorldCapability.Observe,
            principal: session,
            subject: GrantSubject.All
        ).IsAllowed
    );
    /// <summary>Determines whether a principal names a live session of this world: its ordinal is admitted and carries
    /// this epoch.</summary>
    /// <param name="principal">The principal.</param>
    /// <returns><see langword="true"/> when the principal is a live session.</returns>
    public bool IsLiveSession(Principal principal) => (
        IsAdmittedSession(principal: principal) &&
        !m_sessions[principal.Index].ObserverEnded
    );

    // Whether a principal names an admitted session, its observer ended or not: the one an end still applies to.
    private bool IsAdmittedSession(Principal principal) => (
        (principal.Kind == PrincipalKind.Session) &&
        m_sessions.TryGetValue(
            key: principal.Index,
            value: out var entry
        ) &&
        (entry.Principal == principal)
    );
    // Whether a principal is a session that no longer acts: never admitted, ended, or its observer ended.
    private bool IsStaleSession(Principal principal) => (
        (principal.Kind == PrincipalKind.Session) &&
        !IsLiveSession(principal: principal)
    );

    /// <summary>Marks a session whose observer faulted or detached itself: from this moment it is not live, so the grant
    /// table holds nothing for it and every submission naming it is refused as stale, until its end applies at the next
    /// step.</summary>
    /// <param name="session">The session principal.</param>
    internal void MarkObserverEnded(Principal session) {
        if (IsAdmittedSession(principal: session)) {
            m_sessions[session.Index].ObserverEnded = true;
        }
    }
    /// <summary>Admits an unembodied session: the world's own <c>admission</c> policy decides, through the arrival
    /// verdict an in-process authority receives (<see cref="WorldAdmissionDoor.TryAdmitArrival"/>), what a viewer
    /// observing from <paramref name="sourceAuthority"/> is granted. It takes no population entry and no body: only the
    /// verdict's rows that name a subject are minted, onto a fresh session principal; its body-relative rows wait for
    /// <see cref="TryEmbodySession"/>. Called on the thread that steps this world.</summary>
    /// <param name="sourceAuthority">The authority the viewer observes from.</param>
    /// <param name="session">The admitted session principal, on success.</param>
    /// <param name="tier">What the verdict discloses of this world to the session's observation.</param>
    /// <param name="refusal">The named refusal, on failure.</param>
    /// <returns><see langword="true"/> when the session was admitted.</returns>
    internal bool TryAdmitSession(string sourceAuthority, out Principal session, out WorldDisclosureTier tier, out string refusal) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourceAuthority);

        if (WorldAdmissionDoor.TryAdmitArrival(
            entries: Host.Definition.Admission,
            sourceAuthority: sourceAuthority,
            verdict: out var verdict
        ) is { } refused) {
            session = default;
            tier = WorldDisclosureTier.Frames;
            refusal = $"a session observing from '{sourceAuthority}' is refused: {refused}";

            return false;
        }

        tier = verdict!.Tier;

        var ordinal = 0;

        while (m_sessions.ContainsKey(key: ordinal)) {
            ordinal++;
        }

        session = Principal.Session(
            epoch: (m_sessionEpochs.GetValueOrDefault(key: ordinal) + 1),
            ordinal: ordinal
        );
        Host.DispatchServerEvent(
            ordered: false,
            serverEvent: new WorldServerEvent.SessionAdmitted(
                MintedGrants: BuildSessionGrants(
                    session: session,
                    templates: verdict!.Templates
                ),
                Session: session,
                Templates: verdict.Templates
            )
        );
        refusal = string.Empty;

        return true;
    }
    /// <summary>Embodies a live session: mints its verdict's body-relative rows (a <c>Drive</c> row over the body, say)
    /// onto the same principal, beside what it already holds. Allocating the body is the population's; this adds only
    /// the authority, so the body is one no remote peer occupies. Called on the thread that steps this world.</summary>
    /// <param name="session">The session principal.</param>
    /// <param name="bodyIndex">The population body the session now drives.</param>
    /// <param name="refusal">The named refusal, on failure.</param>
    /// <returns><see langword="true"/> when the session was embodied.</returns>
    internal bool TryEmbodySession(Principal session, int bodyIndex, out string refusal) {
        if (!IsLiveSession(principal: session)) {
            refusal = $"{session.Describe()} is not a live session on this world";

            return false;
        }

        var entry = m_sessions[session.Index];

        if (entry.BodyIndex is { } embodied) {
            refusal = $"{session.Describe()} is already embodied in body {embodied}";

            return false;
        }

        if (
            (((uint)bodyIndex) >= ((uint)m_population)) ||
            !Host.Population.IsActive(index: bodyIndex)
        ) {
            refusal = $"body {bodyIndex} is not an active body of this world";

            return false;
        }

        // The same rule the grant door holds for any co-driver: a remote-admitted body is its own peer's alone.
        if (Host.Population.IsAdmittedPeer(bodyIndex: bodyIndex)) {
            refusal = $"body {bodyIndex} is {Host.Population.PeerPrincipal(index: bodyIndex).Describe()}'s own; nothing else drives a remote-admitted body";

            return false;
        }

        Host.DispatchServerEvent(
            ordered: false,
            serverEvent: new WorldServerEvent.SessionEmbodied(
                BodyGeneration: Host.Population.Generation(index: bodyIndex),
                BodyIndex: bodyIndex,
                MintedGrants: BuildEmbodimentGrants(
                    bodyIndex: bodyIndex,
                    session: session,
                    templates: entry.Templates
                ),
                Session: session
            )
        );
        refusal = string.Empty;

        return true;
    }
    /// <summary>Ends an admitted session, one whose observer ended included: revokes every row it holds and retires its
    /// epoch, so a submission still carrying the principal is refused as stale. Called on the thread that steps this world.</summary>
    /// <param name="session">The session principal.</param>
    /// <param name="refusal">The named refusal, on failure.</param>
    /// <returns><see langword="true"/> when the session ended.</returns>
    internal bool EndSession(Principal session, out string refusal) {
        if (!IsAdmittedSession(principal: session)) {
            refusal = $"{session.Describe()} is not a live session on this world";

            return false;
        }

        Host.DispatchServerEvent(
            ordered: false,
            serverEvent: new WorldServerEvent.SessionEnded(
                RevokedGrants: [.. Rows(principal: session)],
                Session: session
            )
        );
        refusal = string.Empty;

        return true;
    }
    /// <summary>Applies an admitted session: registers it under its epoch and installs its rows through the ordinary
    /// grant door.</summary>
    /// <param name="admitted">The event.</param>
    internal void ApplySessionAdmitted(WorldServerEvent.SessionAdmitted admitted) {
        var session = admitted.Session;

        // An epoch never moves backward, so a re-driven admission of an earlier epoch never reopens a later one.
        m_sessionEpochs[session.Index] = Math.Max(
            val1: session.Generation,
            val2: m_sessionEpochs.GetValueOrDefault(key: session.Index)
        );
        // A re-driven admission can land on an ordinal a newer session holds (a replay drive over a world a screen
        // observes): that session ends here, its rows revoked and its observation detached, so it never lingers
        // unobserved beneath the recorded one.
        if (
            m_sessions.TryGetValue(
            key: session.Index,
            value: out var displaced
        ) &&
            (displaced.Principal != session)
        ) {
            Host.Input.Reset(displaced.Principal);
            foreach (var row in Rows(principal: displaced.Principal)) {
                Host.Revoke(
                    actor: Principal.Console,
                    grant: row
                );
            }

            Host.DetachSessionSink(session: displaced.Principal);
        }

        m_sessionOrdinalBound = Math.Max(
            val1: m_sessionOrdinalBound,
            val2: (session.Index + 1)
        );
        m_sessions[session.Index] = new SessionEntry(
            principal: session,
            templates: admitted.Templates
        );

        foreach (var grant in admitted.MintedGrants) {
            _ = TryApplyGrant(
                actor: Principal.Console,
                grant: grant
            );
        }
    }
    /// <summary>Applies an embodiment: records the body and installs the body-relative rows.</summary>
    /// <param name="embodied">The event.</param>
    internal void ApplySessionEmbodied(WorldServerEvent.SessionEmbodied embodied) {
        var entry = m_sessions[embodied.Session.Index];

        if (entry.BodyIndex != embodied.BodyIndex || entry.BodyGeneration != embodied.BodyGeneration) { Host.Input.Reset(embodied.Session); }

        entry.BodyIndex = embodied.BodyIndex;
        entry.BodyGeneration = embodied.BodyGeneration;

        foreach (var grant in embodied.MintedGrants) {
            _ = TryApplyGrant(
                actor: Principal.Console,
                grant: grant
            );
        }
    }
    /// <summary>Applies an ended session: revokes its rows and forgets it, keeping its epoch retired.</summary>
    /// <param name="ended">The event.</param>
    internal void ApplySessionEnded(WorldServerEvent.SessionEnded ended) {
        Host.Input.Reset(ended.Session);
        foreach (var grant in ended.RevokedGrants) {
            Host.Revoke(
                actor: Principal.Console,
                grant: grant
            );
        }

        _ = m_sessions.Remove(key: ended.Session.Index);
        Host.DetachSessionSink(session: ended.Session);
    }
    /// <summary>Describes every live session as the events that would re-establish it as it stands: its admission,
    /// carrying every row it holds now, then its embodiment when it has one. A replay tape armed while sessions live
    /// opens with these, since the tape reproduces only the lifecycle events it saw.</summary>
    /// <returns>The events, in ordinal order.</returns>
    internal IReadOnlyList<WorldServerEvent> LiveSessionEvents() {
        var events = new List<WorldServerEvent>();

        foreach (var entry in m_sessions.OrderBy(keySelector: static pair => pair.Key).Select(selector: static pair => pair.Value)) {
            events.Add(item: new WorldServerEvent.SessionAdmitted(
                MintedGrants: [.. Rows(principal: entry.Principal)],
                Session: entry.Principal,
                Templates: entry.Templates
            ));

            if (entry.BodyIndex is { } body) {
                events.Add(item: new WorldServerEvent.SessionEmbodied(
                    BodyGeneration: entry.BodyGeneration,
                    BodyIndex: body,
                    MintedGrants: [],
                    Session: entry.Principal
                ));
            }
        }

        return events;
    }
    /// <summary>Lists the live sessions, in ordinal order.</summary>
    /// <returns>Each live session's principal.</returns>
    internal List<Principal> LiveSessionPrincipals() => [.. m_sessions.OrderBy(keySelector: static pair => pair.Key).Select(selector: static pair => pair.Value.Principal).Where(predicate: principal => IsLiveSession(principal: principal))];

    /// <summary>Gets one past the highest ordinal a session ever took on this world — the bound a read over every live
    /// session walks, in ordinal order.</summary>
    public int SessionOrdinalBound => m_sessionOrdinalBound;

    /// <summary>Latches one forwarded input for a live session: its pointer ray and channels become the session's
    /// current input, held until the next forward, and each channel's press is kept until the step's rules have read
    /// it (<see cref="SettleSessionPresses"/>), so a press and release that both arrive between two steps still reach
    /// one step's rules. A submission naming a session that is not live is refused.</summary>
    /// <param name="submission">The submission, whose principal is the session.</param>
    /// <returns><see langword="true"/> when the input latched.</returns>
    internal bool TryLatchSessionInput(in IntentSubmission submission) {
        if (!IsLiveSession(principal: submission.Principal)) {
            return false;
        }

        var entry = m_sessions[submission.Principal.Index];

        if (!Host.Input.TryAdmit(submission.Principal, submission.Input, out var reason, -1)) {
            Host.Output.Narrate(channel: "input.refused", text: reason);
            return false;
        }
        _ = Host.Input.TryGet(submission.Principal, out var owner);
        var image = owner!.Image;

        entry.Input = image;

        var strengthened = false;

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            var value = image[ordinal];

            if (FixedQ4816.Abs(value: value) > FixedQ4816.Abs(value: entry.PressedSince[ordinal])) {
                entry.PressedSince[ordinal] = value;
                strengthened = true;
            }
        }

        if (strengthened) {
            entry.PressedRay = image.SourceRay;
        }

        {
            foreach (var inputEvent in submission.Input.Events.Span) {
                if (inputEvent.Kind != WorldInputEventKind.Press) { continue; }
                var address = owner.Mapping.Address(inputEvent.Channel);
                if (address < 0) { continue; }
                entry.PressedSince[address] = FixedQ4816.One;
                entry.PressedRay = image.SourceRay;
            }
        }

        return true;
    }
    /// <summary>Installs an epoch barrier in the session pointer readers as well as its timed input owner.</summary>
    internal void InstallSessionInputBarrier(Principal principal, PlayerIntent image) {
        if (!IsLiveSession(principal)) { return; }
        var entry = m_sessions[principal.Index];
        entry.Input = image;
        entry.PressedSince = default;
        entry.PressedRay = null;
    }
    /// <summary>Clears every session's kept presses once a step's rules have read them; the current input stays.</summary>
    internal void SettleSessionPresses() {
        foreach (var entry in m_sessions.Values) {
            entry.PressedSince = default;
            entry.PressedRay = null;
        }
    }

    /// <summary>Reads the input of the live session at an ordinal: its pointer ray this step, which is the ray a press it
    /// still keeps was made along while its current input no longer carries that press, and its current ray otherwise.</summary>
    /// <param name="ordinal">The session ordinal.</param>
    /// <param name="session">The live session principal, on success.</param>
    /// <param name="ray">The session's pointer ray, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when a live session holds the ordinal.</returns>
    public bool TryReadSessionPointer(int ordinal, out Principal session, out SourceRay? ray) {
        if (m_sessions.TryGetValue(
            key: ordinal,
            value: out var entry
        )) {
            session = entry.Principal;
            ray = (KeepsPress(entry: entry)
                ? entry.PressedRay
                : entry.Input.SourceRay);

            return true;
        }

        session = default;
        ray = null;

        return false;
    }
    /// <summary>Reads a live session's channel this step: its current value, or the stronger press it made since the
    /// last step, whichever has the larger magnitude.</summary>
    /// <param name="ordinal">The session ordinal.</param>
    /// <param name="channel">The logical channel id.</param>
    /// <returns>The channel's value, or zero when no live session holds the ordinal.</returns>
    public FixedQ4816 ReadSessionChannel(int ordinal, int channel) {
        if (!m_sessions.TryGetValue(
            key: ordinal,
            value: out var entry
        )) {
            return FixedQ4816.Zero;
        }

        var mapping = Host.Input.TryGet(entry.Principal, out var input)
            ? input!.Mapping : Host.Population.InputCatalog.DefaultMapping;
        var address = mapping.Address(channel);
        if (address < 0) { return FixedQ4816.Zero; }
        Host.Input.ObserveHeld(entry.Principal, (ushort)channel);
        var current = input is null ? entry.Input[address] : input.Image[address];
        var pressed = entry.PressedSince[address];

        return ((FixedQ4816.Abs(value: pressed) > FixedQ4816.Abs(value: current))
            ? pressed
            : current);
    }

    /// <summary>Ends every live session at a rebuild, detaching each observation: the reset wiped their rows, and no
    /// template survives it. Their epochs stay retired, so a screen observing through one admits a new session under
    /// the candidate's policy.</summary>
    internal void EndSessionsForRebuild() {
        foreach (var session in m_sessions.Values) { Host.Input.Reset(session.Principal); }
        m_sessions.Clear();
        Host.DetachSessionSinks();
    }
    /// <summary>Revokes the rows any session holds over a body a later generation now occupies, and forgets that
    /// embodiment: the incarnation it drove has left, so it never drives the next occupant. Every door that activates
    /// a body under a new generation calls this once the body is live: a peer admission's server event (beside the
    /// stale peer generations <see cref="StalePeerGenerations"/> names for the same body) and a seat join. A resumed
    /// body keeps its generation, and with it the session's rows.</summary>
    /// <param name="index">The body index a generation took.</param>
    internal void RevokeStaleEmbodiments(int index) {
        var generation = Host.Population.Generation(index: index);
        var body = GrantSubject.Body(index: index);

        foreach (var entry in m_sessions.Values) {
            if (
                (entry.BodyIndex != index) ||
                (entry.BodyGeneration == generation)
            ) {
                continue;
            }

            foreach (var row in Rows(principal: entry.Principal).Where(predicate: row => (row.Subject == body)).ToList()) {
                Host.Revoke(
                    actor: Principal.Console,
                    grant: row
                );
            }

            entry.BodyIndex = null;
        }
    }

    // Whether a session keeps a press its current input has since released: a channel pressed harder since the last
    // settled step than it is held now.
    private static bool KeepsPress(SessionEntry entry) {
        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (FixedQ4816.Abs(value: entry.PressedSince[ordinal]) > FixedQ4816.Abs(value: entry.Input[ordinal])) {
                return true;
            }
        }

        return false;
    }

    // One live session: the principal it acts as, the verdict's templates, the body (and that body's generation) an
    // embodiment assigned it, and the input its viewer forwards.
    private sealed class SessionEntry(Principal principal, IReadOnlyList<WorldAdmissionGrant> templates) {
        // The largest-magnitude value each channel reached since the last settled step (SettleSessionPresses).
        public ChannelValues PressedSince;

        public int BodyGeneration { get; set; }
        public int? BodyIndex { get; set; }
        // Set the moment the session's observer faulted or detached itself: it acts no more, though its end applies at
        // the next step.
        public bool ObserverEnded { get; set; }
        // The input the session's viewer last forwarded: its pointer ray in this world and its channels, held until the
        // next forward replaces it (a release forward clears it).
        public PlayerIntent Input { get; set; }
        // The pointer ray the strongest press since the last settled step was made along.
        public SourceRay? PressedRay { get; set; }

        public Principal Principal { get; } = principal;
        public IReadOnlyList<WorldAdmissionGrant> Templates { get; } = templates;
    }
}
