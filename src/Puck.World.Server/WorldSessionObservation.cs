using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One unembodied session observing a world, as <see cref="WorldServer.TryObserveAsSession"/> admitted it.
/// Disposing it ends the session, which revokes its rows and detaches its sink.</summary>
public sealed class WorldSessionObservation : IDisposable {
    internal const string QueryRefusal = "[query refused: session disclosure does not carry this readback]";

    private readonly WorldServer m_server;

    private WorldSessionSink? m_sink;
    private volatile bool m_ended;
    private bool m_disposed;

    internal WorldSessionObservation(WorldServer server, Principal session, WorldDisclosureTier tier, string sourceAuthority) {
        m_server = server;
        Session = session;
        Tier = tier;
        SourceAuthority = sourceAuthority;
    }

    /// <summary>Gets a value indicating whether the observation ended: disposed, ended by its world or a rebuild, or
    /// detached for faulting. An ended observation is delivered nothing further.</summary>
    public bool Ended => m_ended;
    /// <summary>Gets the session principal the observation acts as.</summary>
    public Principal Session { get; }
    /// <summary>Gets what the admission verdict discloses of the world to this observation.</summary>
    public WorldDisclosureTier Tier { get; }
    /// <summary>Gets the authority the viewer observes from, which the world's admission rows are asked about.</summary>
    public string SourceAuthority { get; }

    /// <summary>Returns whether this observation observes a world: the one server it was admitted by.</summary>
    /// <param name="server">The server.</param>
    /// <returns><see langword="true"/> when the observation was admitted by <paramref name="server"/>.</returns>
    public bool Observes(WorldServer server) => ReferenceEquals(
        objA: m_server,
        objB: server
    );

    /// <summary>Gets a value indicating whether the observation is delivered everything its world holds right now: it has
    /// not ended, it still observes the whole world, its admission discloses a <see cref="WorldDisclosureTier.Replica"/>,
    /// and its world's observer disclosure redacts no body. Only then may a viewer render its world's own whole replica
    /// in its place (a portal window joining its destination's endpoint scene), since that replica shows it nothing its
    /// own delivery would not. Read on the thread that steps the observed world.</summary>
    public bool DisclosesEverything => (
        !m_ended &&
        (Tier == WorldDisclosureTier.Replica) &&
        m_server.ObservesAsSession(session: Session) &&
        new WorldSinkDisclosure(
            ObserverBodyIndex: -1,
            Policy: m_server.Definition.Population.ObserverDisclosure
        ).IsFull
    );

    internal void Attach(WorldSessionSink sink) => m_sink = sink;
    internal bool AllowsQuery(WorldQuery query) => (!m_ended && (m_sink?.AllowsQuery(query: query) == true));
    internal WorldDefinition? ReadDefinition() => (AllowsQuery(query: new WorldQuery.StateObservations())
        ? m_sink!.Disclose(definition: m_server.Definition)
        : null);
    // An ended observation is delivered nothing further, so its sink's feed lets go of what it held for it.
    internal void MarkEnded() {
        m_ended = true;
        m_sink?.Release();
    }

    /// <summary>Discloses a candidate definition as it would reach this observation's renderer, for a consumer that
    /// measures a world document for that renderer on the observed world's side (a render envelope sizing the
    /// observer's view, say). The tier is the one the candidate's own admission rows give this viewer, so a candidate
    /// that re-tiers the viewer (a rebuild) is measured as the session a screen admits next would see it, and a
    /// candidate admitted while observation is withheld still fits once delivered. Nothing measured reaches the
    /// observer. Called on the thread that steps the observed world.</summary>
    /// <param name="candidate">The candidate definition.</param>
    /// <returns>The disclosed definition, or <see langword="null"/> when nothing of it could reach the observer: a
    /// candidate that refuses the viewer or discloses it frames alone.</returns>
    /// <exception cref="WorldRenderMeasureRefusedException">The candidate's store cannot be laid out, or its projection
    /// cannot be composed: a measurer refuses it rather than measuring nothing.</exception>
    public WorldDefinition? Disclose(WorldDefinition candidate) {
        ArgumentNullException.ThrowIfNull(argument: candidate);

        if (
            (m_sink is not { } sink) ||
            (WorldAdmissionDoor.TryAdmitArrival(
            entries: candidate.Admission,
            sourceAuthority: SourceAuthority,
            verdict: out var verdict
        ) is not null)
        ) {
            return null;
        }

        if (verdict!.Tier == WorldDisclosureTier.Frames) {
            return null;
        }

        // The candidate's own store, laid out with the lanes validation lays it out with, at this world's clock: a
        // projection reads the audiences and values the candidate declares, not the ones the live document still holds.
        // Which session a screen admits next, and which reader lists name it, is not known, so the projection is the
        // upper bound: every reader restriction admitted, and every dealt child showing the costliest prototype its
        // deal can deal to some reader. Any failure to lay out or compose it is the candidate's
        // refusal, never a throw through the envelope or the step.
        var time = m_server.Time;

        try {
            if (!StateArena.TryCreate(
                arena: out var arena,
                catalog: candidate.StateCatalog,
                options: WorldSlotLanes.Options(definition: candidate),
                reason: out var reason,
                section: candidate.StateRaw,
                time: in time
            )) {
                throw new WorldRenderMeasureRefusedException(message: $"the candidate's state does not load into a store to disclose it to {Session.Describe()}: {reason}");
            }

            return ((sink.Disclose(
                arena: arena,
                definition: candidate,
                recipient: null,
                tier: verdict.Tier,
                unrestricted: true
            ) is { } disclosed)
                ? WorldPlacementDeal.WithCostliestChildren(definition: disclosed)
                : null);
        } catch (Exception exception) when ((exception is not WorldRenderMeasureRefusedException)) {
            throw new WorldRenderMeasureRefusedException(
                innerException: exception,
                message: $"the candidate cannot be disclosed to {Session.Describe()}: {exception.Message}"
            );
        }
    }
    /// <summary>Ends the session, whether or not its observation already ended. A world already retiring takes its
    /// sessions with it.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        try {
            m_server.ExecuteAuthorityOperation(operation: () => {
                _ = m_server.EndSession(
                    refusal: out _,
                    session: Session
                );
                m_server.DetachSessionSink(session: Session);
            });
        } catch (InvalidOperationException) {
            // The activation froze for retirement; its sessions and sinks end with it.
        }

        m_ended = true;
    }
}
