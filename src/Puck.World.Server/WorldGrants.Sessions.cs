using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    // The live unembodied sessions this world admitted, by ordinal, and the epoch each ordinal last carried. The epochs
    // outlive the sessions, so a reused ordinal's next session carries a later epoch and a principal naming a retired
    // one never matches a live session again. A session is the screen observing through it: no checkpoint holds one
    // (see Capture), and a rebuild ends every one (EndSessionsForRebuild).
    private readonly Dictionary<int, SessionEntry> m_sessions = new();
    private readonly Dictionary<int, int> m_sessionEpochs = new();

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
    // Whether a grantee is a session, whose rows the checkpoint leaves out.
    private static bool IsSessionGrantee(Grantee grantee) => (
        grantee.TryGetPrincipal(principal: out var principal) &&
        (principal.Kind == PrincipalKind.Session)
    );

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
        !m_sessions[principal.Index].Faulted
    );

    // Whether a principal names an admitted session, faulted or not: the one an end still applies to.
    private bool IsAdmittedSession(Principal principal) => (
        (principal.Kind == PrincipalKind.Session) &&
        m_sessions.TryGetValue(
            key: principal.Index,
            value: out var entry
        ) &&
        (entry.Principal == principal)
    );
    // Whether a principal is a session that no longer acts: never admitted, ended, or faulted.
    private bool IsStaleSession(Principal principal) => (
        (principal.Kind == PrincipalKind.Session) &&
        !IsLiveSession(principal: principal)
    );

    /// <summary>Marks a session whose observer faulted: from this moment it is not live, so the grant table holds
    /// nothing for it and every submission naming it is refused as stale, until its end applies at the next step.</summary>
    /// <param name="session">The session principal.</param>
    internal void MarkSessionFaulted(Principal session) {
        if (IsAdmittedSession(principal: session)) {
            m_sessions[session.Index].Faulted = true;
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
    /// <summary>Ends an admitted session, a faulted one included: revokes every row it holds and retires its epoch, so a
    /// submission still carrying the principal is refused as stale. Called on the thread that steps this world.</summary>
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
            foreach (var row in Rows(principal: displaced.Principal)) {
                Host.Revoke(
                    actor: Principal.Console,
                    grant: row
                );
            }

            Host.DetachSessionSink(session: displaced.Principal);
        }

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
    /// <summary>Ends every live session at a rebuild, detaching each observation: the reset wiped their rows, and no
    /// template survives it. Their epochs stay retired, so a screen observing through one admits a new session under
    /// the candidate's policy.</summary>
    internal void EndSessionsForRebuild() {
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

    // One live session: the principal it acts as, the verdict's templates, and the body (and that body's generation)
    // an embodiment assigned it.
    private sealed class SessionEntry(Principal principal, IReadOnlyList<WorldAdmissionGrant> templates) {
        public int BodyGeneration { get; set; }
        public int? BodyIndex { get; set; }
        // Set the moment the session's observer faulted: it acts no more, though its end applies at the next step.
        public bool Faulted { get; set; }

        public Principal Principal { get; } = principal;
        public IReadOnlyList<WorldAdmissionGrant> Templates { get; } = templates;
    }
}
