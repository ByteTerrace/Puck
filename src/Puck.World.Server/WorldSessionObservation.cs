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
        View = server.DeliveryView(session, tier, () => CanObserve);
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

    /// <summary>Gets whether this observation may receive scene data now: it has not ended, its tier carries a
    /// document, and its current live session still holds observe all. Withholding data does not end the observation;
    /// the same recorded session can resume it when its grant returns.</summary>
    public bool CanObserve => (!m_ended && (Tier != WorldDisclosureTier.Frames) && m_server.ObservesAsSession(session: Session));

    internal WorldRecipientView View { get; }

    /// <summary>Returns whether this observation observes a world: the one server it was admitted by.</summary>
    /// <param name="server">The server.</param>
    /// <returns><see langword="true"/> when the observation was admitted by <paramref name="server"/>.</returns>
    public bool Observes(WorldServer server) => ReferenceEquals(
        objA: m_server,
        objB: server
    );

    /// <summary>Gets the authority's current proof about the scene inputs this observation's stream is handed, or
    /// <see langword="null"/> while it is handed none a viewer may share: it has ended or stopped observing, or its
    /// admission discloses less than a <see cref="WorldDisclosureTier.Replica"/>. Issued at each delivery
    /// (<see cref="WorldSceneProof"/>). Read on the thread that steps the observed world.</summary>
    public WorldSceneProof? SceneProof => (CanObserve ? m_sceneProof : null);

    private WorldSceneProof? m_sceneProof;

    // Issues the stream's proof for the delivery it has just been handed at an authority tick: the current proof stands
    // while its identity and its standing beside a whole reader hold, and is closed for a new one otherwise. A row no
    // scene reads never moves the identity, so it never refuses sharing.
    internal void SettleSceneProof(ulong tick) {
        var scene = (CanObserve ? View.SceneIdentity : null);
        if (scene is not { } identity) {
            CloseSceneProof(end: tick);
            return;
        }
        var shares = (RedactsNoBody(server: m_server) && (identity == m_server.WholeSceneIdentity()));
        if ((m_sceneProof is { } held) && (held.Scene == identity) && (held.SharesAuthority == shares)) { return; }
        var previous = m_sceneProof;
        m_sceneProof?.Close(end: tick);
        m_sceneProof = new WorldSceneProof(first: tick, scene: identity, server: m_server, sharesAuthority: shares);
        if ((previous?.SharesAuthority ?? !shares) != shares) {
            m_server.Output.Narrate(channel: "world.session", text: (shares
                ? $"[world.session: {Session.Describe()} is handed its world's whole scene; a window may draw it from that world's residency]"
                : $"[world.session: {Session.Describe()} is handed less than its world's whole scene ({WhyNotWhole()}); a window draws it from the residency of the sessions handed scene {identity.Hex[..12]}]"));
        }
    }
    // Ends the stream's proof: it stops observing, so nothing it was handed may be shared from now.
    internal void CloseSceneProof(ulong end) {
        m_sceneProof?.Close(end: end);
        m_sceneProof = null;
    }
    // Why the stream's scene differs from a whole reader's, for the operator: a body redacted, a scene row withheld, or
    // the placements as it is dealt them.
    private string WhyNotWhole() => (!RedactsNoBody(server: m_server)
        ? "its world's observer disclosure redacts bodies"
        : ((View.FirstSceneRowWithheld is { } row)
            ? $"row '{row}', which its scene reads, withholds from it"
            : "its placements are dealt from rows it does not read whole"));
    // Whether the world's live observer disclosure hands a session every body as the authority holds it.
    internal static bool RedactsNoBody(WorldServer server) => new WorldSinkDisclosure(
        ObserverBodyIndex: -1,
        Policy: server.Definition.Population.ObserverDisclosure
    ).IsFull;

    internal void Attach(WorldSessionSink sink) => m_sink = sink;
    internal bool AllowsQuery(WorldQuery query) => (!m_ended && (m_sink?.AllowsQuery(query: query) == true));
    // An ended observation is delivered nothing further, so its sink's feed lets go of what it held for it and its
    // observer is told the stream ended. The first end names the cause its observer is told.
    internal void MarkEnded(string reason) {
        var first = !m_ended;

        m_ended = true;
        m_sink?.End();
        if (first) { EndedBecause?.Invoke(obj: reason); }
    }

    /// <summary>Raised once, on the thread that ends the observation, when it ends for any cause, with that cause:
    /// the stream-lifetime event its observer receives at the tick it occurs.</summary>
    public event Action<string>? EndedBecause;

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
        var time = m_server.DeliveryTime;

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
                m_server.DetachSessionSink(reason: "world.session.disposed", session: Session);
            });
        } catch (InvalidOperationException) {
            // The activation froze for retirement; its sessions and sinks end with it.
        }

        m_ended = true;
    }
}
