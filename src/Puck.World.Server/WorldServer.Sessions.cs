using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>The refusal code a submission carries when its principal names a session this world does not hold live:
    /// one that ended, whose epoch is retired, or one it never admitted.</summary>
    public const string StaleSessionCode = "world.session.stale";

    // Each live session's observation: its hub lease and the handle its observer holds.
    private readonly Dictionary<Principal, (IDisposable Lease, WorldSessionObservation Observation)> m_sessionSinks = new();
    // Sessions whose observer faulted during a delivery, ended at the start of the next step: ending one inside the
    // hub's fan-out would tape its end after the tick's own entries rather than where a replay applies it.
    private readonly List<Principal> m_faultedSessions = [];

    /// <inheritdoc cref="WorldGrants.EndSession"/>
    public bool EndSession(Principal session, out string refusal) => m_grants.EndSession(
        refusal: out refusal,
        session: session
    );
    /// <inheritdoc cref="WorldGrants.IsLiveSession"/>
    public bool IsLiveSession(Principal principal) => m_grants.IsLiveSession(principal: principal);
    /// <inheritdoc cref="WorldGrants.ObservesAsSession"/>
    public bool ObservesAsSession(Principal session) => m_grants.ObservesAsSession(session: session);
    /// <inheritdoc cref="WorldGrants.TryAdmitSession"/>
    public bool TryAdmitSession(string sourceAuthority, out Principal session, out WorldDisclosureTier tier, out string refusal) => m_grants.TryAdmitSession(
        refusal: out refusal,
        session: out session,
        sourceAuthority: sourceAuthority,
        tier: out tier
    );
    /// <summary>Observes this world as an unembodied session: admits one through this world's own <c>admission</c>
    /// policy for a viewer observing from <paramref name="sourceAuthority"/>, then attaches <paramref name="sink"/> as
    /// that session's observation. The sink is delivered what the verdict's tier discloses (the definition verbatim at
    /// <see cref="WorldDisclosureTier.Replica"/>, a projection composed for the session at
    /// <see cref="WorldDisclosureTier.Presentation"/>, nothing at <see cref="WorldDisclosureTier.Frames"/>), and only
    /// while the session holds <c>observe all</c>, with each snapshot redacted under this world's live observer
    /// disclosure. Ending the session, by disposing the returned observation, by a rebuild, or by the sink faulting,
    /// detaches the sink. Safe from any thread: it runs under this world's authority gate.</summary>
    /// <param name="sourceAuthority">The authority the viewer observes from.</param>
    /// <param name="sink">The observation's sink, delivered the disclosed definition before this returns.</param>
    /// <param name="refusal">The named refusal, on failure.</param>
    /// <returns>The observation, or <see langword="null"/> when this world refuses the session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public WorldSessionObservation? TryObserveAsSession(string sourceAuthority, IClientSink sink, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        var (observation, reason) = ExecuteAuthorityOperation(operation: () => {
            if (!m_grants.TryAdmitSession(
                refusal: out var refused,
                session: out var session,
                sourceAuthority: sourceAuthority,
                tier: out var tier
            )) {
                return (Observation: ((WorldSessionObservation?)null), Reason: refused);
            }

            var admitted = new WorldSessionObservation(
                server: this,
                session: session,
                sourceAuthority: sourceAuthority,
                tier: tier
            );
            var observed = new WorldSessionSink(
                inner: sink,
                observation: admitted,
                server: this
            );

            admitted.Attach(sink: observed);

            // Unfiltered at the hub: the session sink redacts each snapshot under the world's live observer policy
            // itself, so a policy edit reaches the next delivery rather than the one frozen at attach. The hub refuses
            // an attach from inside its own delivery; the session just admitted ends with the refusal.
            IDisposable lease;

            try {
                lease = m_document.AttachSink(sink: observed);
            } catch (InvalidOperationException exception) {
                _ = m_grants.EndSession(
                    refusal: out _,
                    session: session
                );
                admitted.MarkEnded();

                return (Observation: ((WorldSessionObservation?)null), Reason: exception.Message);
            }

            m_sessionSinks[session] = (lease, admitted);

            // A primer the observer faulted on detached it before it was ever delivered a tick: the session ends
            // rather than standing admitted with nothing observing through it.
            if (observed.Fault is { } fault) {
                _ = m_grants.EndSession(
                    refusal: out _,
                    session: session
                );
                DetachSessionSink(session: session);

                return (Observation: ((WorldSessionObservation?)null), Reason: $"the observer faulted on its first delivery: {fault.Message}");
            }

            return (Observation: admitted, Reason: string.Empty);
        });

        refusal = reason;

        return observation;
    }

    /// <summary>Detaches a session's observation, marking it ended; nothing when it has none.</summary>
    /// <param name="session">The session principal.</param>
    internal void DetachSessionSink(Principal session) {
        if (m_sessionSinks.Remove(
            key: session,
            value: out var attached
        )) {
            attached.Lease.Dispose();
            attached.Observation.MarkEnded();
        }
    }
    /// <summary>Notes a session whose observer faulted during a delivery; the next step ends it.</summary>
    /// <param name="session">The session principal.</param>
    internal void NoteFaultedSession(Principal session) => m_faultedSessions.Add(item: session);
    /// <summary>Ends every session whose observer faulted since the last step, through the one end door: its rows are
    /// revoked and its observation detached, as if its observer had released it. Called at the start of a step.</summary>
    internal void EndFaultedSessions() {
        if (m_faultedSessions.Count == 0) {
            return;
        }

        foreach (var session in m_faultedSessions) {
            _ = m_grants.EndSession(
                refusal: out _,
                session: session
            );
            DetachSessionSink(session: session);
        }

        m_faultedSessions.Clear();
    }
    /// <summary>Ends every live session no observer holds, through the one end door: a session a replay drive restored
    /// from its tape, whose recorded viewer is not watching this world, so nothing it last pressed or pointed at stays
    /// held. A session admits only through an observation (<see cref="TryObserveAsSession"/>), so one without an attached
    /// observer has nobody left to end it. Called as a drive ends.</summary>
    internal void EndUnobservedSessions() {
        foreach (var session in m_grants.LiveSessionPrincipals()) {
            if (!m_sessionSinks.ContainsKey(key: session)) {
                _ = m_grants.EndSession(
                    refusal: out _,
                    session: session
                );
            }
        }
    }
    /// <summary>Detaches every session's observation.</summary>
    internal void DetachSessionSinks() {
        foreach (var session in m_sessionSinks.Keys.ToList()) {
            DetachSessionSink(session: session);
        }
    }

    /// <inheritdoc cref="WorldGrants.TryEmbodySession"/>
    public bool TryEmbodySession(Principal session, int bodyIndex, out string refusal) => m_grants.TryEmbodySession(
        bodyIndex: bodyIndex,
        refusal: out refusal,
        session: session
    );
}
