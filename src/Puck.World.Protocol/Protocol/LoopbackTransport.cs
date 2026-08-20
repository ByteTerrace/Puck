namespace Puck.World.Protocol;

/// <summary>The in-process transport binding one client to one <see cref="IWorldServerHost"/> (a
/// <c>Puck.World.Server.WorldServer</c>, always, but this project names it only through the interface).</summary>
/// <remarks>
/// Every non-intent submission (command/grant/revoke/session/rebuild/mutation/undo/composition/lever/query/addon-
/// lifecycle) travels as one <see cref="SubmissionEnvelope"/> through <see cref="IWorldServerHost.Submit"/> — the
/// server's single ordered domain — which this transport enqueues and drains inline, on the tick thread, before a
/// <c>Submit*</c> call returns (the host's command-apply window immediately precedes the tick's step, so FIFO order
/// and read-after-write are preserved — a byte transport would buffer to the same boundary instead). Per-tick
/// intents buffer separately. The produced <see cref="WorldSnapshot"/> is pushed to every attached
/// <see cref="IClientSink"/>. Single-threaded on the host tick. Every submission crosses <see cref="WorldFrameCodec"/>'s
/// canonical encode-then-decode path even when no replay is armed; loopback is a transport optimization, never a
/// second object-only protocol.
/// <para>
/// Every record tap fires immediately before its write reaches the server (before <see cref="IWorldServerHost.Submit"/>
/// is called), so the tape captures the submission stream in the exact order the server saw it — including the
/// interleaving between a driving command and a grant change, which is the coordinate an authority verdict is pinned
/// against. Every ordered-domain payload kind a client can submit has a tap here or an apply-time twin on the server
/// (<c>WorldServer.MutationTap</c>/<c>RebuildTap</c>/<c>ScreenOpTap</c>), so the tape's capture scope is the whole
/// submission surface rather than a chosen subset. A kind whose submissions can also arrive from a socket peer or a
/// federation forwarder belongs on the SERVER twin, never here — this transport sees only the local connection.
/// </para>
/// </remarks>
public sealed class LoopbackTransport : IServerLink {
    private readonly IWorldServerHost m_server;

    private long m_correlationId;
    // The local connection's per-connection monotonic Sequence and the CorrelationId every envelope mints — both
    // simple auto-incrementing counters today (nothing consults Sequence for backpressure over loopback, and nothing
    // correlates a remote reply against CorrelationId yet); the wire transport will need real ones, this one just has
    // to be MONOTONIC so the envelope shape is honest.
    private long m_sequence;

    /// <summary>Initializes a new instance of the <see cref="LoopbackTransport"/> class over the server it fronts.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public LoopbackTransport(IWorldServerHost server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        m_server = server;
    }

    /// <summary>Gets an optional record tap invoked with every addon-lifecycle submission before it reaches the server,
    /// carrying the action and the actor that submitted it — the same reasoning <see cref="GrantTap"/> carries: a
    /// replay whose fresh world never re-mounted (or re-unmounted) a guest re-drives a differently-composed
    /// simulation. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldAddonLifecycle, WorldPrincipal>? AddonLifecycleTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every window-composition submission before it reaches the
    /// server, carrying the composition and its actor. <see langword="null"/> (the default) is a free
    /// pass-through.</summary>
    public Action<WorldComposition, WorldPrincipal>? CompositionTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every authority command before it applies — one kind of entry in
    /// the captured per-tick authority stream. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldCommand>? CommandTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every designation before it applies.</summary>
    public Action<WorldDesignation, WorldPrincipal>? DesignationTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every grant acquisition before it applies, carrying the grant row
    /// and the actor that asked for it. Captured for the same reason commands are: authority is an input to the tick,
    /// and a replay whose fresh world was never granted re-drives a differently-authorized simulation — which bites
    /// hardest on the addon path, where the re-run guest is checked against the replayed world's own table.
    /// <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldGrant, WorldPrincipal>? GrantTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every read-back query before it reaches the server, carrying
    /// the query and the identity the envelope stamped. Captured because a query crosses the same Observe gate a
    /// grant change moves, so a replay that skipped it would exercise a different admission history.
    /// <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldQuery, WorldPrincipal>? QueryTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every submitted intent before it reaches the server — the seam the
    /// replay tape captures the live per-tick intent stream through. <see langword="null"/> (the default) is a free
    /// pass-through; set only while a recording is armed.</summary>
    public Action<IntentSubmission>? IntentTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every revocation before it applies — the mirror of
    /// <see cref="GrantTap"/>. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldGrant, WorldPrincipal>? RevokeTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every session request before it applies. Occupancy, profile, and
    /// population changes are authoritative inputs to later simulation ticks.</summary>
    public Action<SessionRequest>? SessionTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every journal undo before it reaches the server, carrying the
    /// entry count and its actor. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<int, WorldPrincipal>? UndoTap { get; set; }

    // Mints the next envelope for the LOCAL connection (id 0, generation 0) — Sequence/CorrelationId both simple
    // monotonic counters (see their own field remarks).
    private void Submit(WorldPrincipal principal, WorldSubmissionPayload payload) {
        if (TryNextEnvelope(
            envelope: out var envelope,
            payload: payload,
            principal: principal
        )) {
            m_server.Submit(envelope: envelope);
        }
    }
    // The ALWAYS-BYTES rule: even the in-process link is defined by the same canonical frame a future socket carries.
    // A refusal is a transport verdict printed by name; invalid caller state never escapes as an invariant exception.
    private bool TryNextEnvelope(WorldPrincipal principal, WorldSubmissionPayload payload, out SubmissionEnvelope envelope) {
        if (
            !WorldFrameCodec.TryEncode(
            failure: out var failure,
            frame: out var frame,
            payload: payload
        ) ||
            !WorldFrameCodec.TryDecode(
            failure: out failure,
            frame: frame,
            payload: out var decoded
        ) ||
            (decoded is null)
        ) {
            Console.Error.WriteLine(value: $"[world.codec refused: {failure}]");
            envelope = default;
            return false;
        }

        envelope = new SubmissionEnvelope(
            ConnectionId: SubmissionEnvelope.LocalConnectionId,
            SessionGeneration: 0,
            Sequence: ++m_sequence,
            CorrelationId: ++m_correlationId,
            Principal: principal,
            Payload: decoded
        );

        return true;
    }
    // Encodes and decodes a typed payload, taps its canonical value with the envelope's principal, then submits it.
    // The payload's concrete leaf type proves that decoding returned the expected union case.
    private void SubmitTapped<TPayload, TValue>(TPayload payload, WorldPrincipal principal, Func<TPayload, TValue> selectValue, Action<TValue, WorldPrincipal>? tap) where TPayload : WorldSubmissionPayload {
        if (
            TryNextEnvelope(
            envelope: out var envelope,
            payload: payload,
            principal: principal
        ) &&
            (envelope.Payload is TPayload canonical)
        ) {
            tap?.Invoke(
                arg1: selectValue(arg: canonical),
                arg2: envelope.Principal
            );
            m_server.Submit(envelope: envelope);
        }
    }

    /// <summary>Binds the client sink the server delivers each tick's snapshot to.</summary>
    /// <param name="sink">The client sink.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed — see
    /// <see cref="IWorldServerHost.AttachSink"/>'s own remarks for the threading and disposal contract.</returns>
    public IDisposable Bind(IClientSink sink) {
        return m_server.AttachSink(sink: sink);
    }
    /// <inheritdoc/>
    public void Query(WorldQuery query, Action<QueryAnswer> completion) {
        ArgumentNullException.ThrowIfNull(argument: completion);

        // Queries carry no principal of their own; the envelope is the identity coordinate. In-process read-backs
        // are trusted console/script readers, so loopback stamps Console. WorldTcpHost stamps its admitted peer
        // instead, and WorldServer applies the same Observe gate to both before composing the answer.
        if (TryNextEnvelope(
            principal: WorldPrincipal.Console,
            payload: new WorldSubmissionPayload.Query(Value: query),
            envelope: out var envelope
        )) {
            if (envelope.Payload is WorldSubmissionPayload.Query canonical) {
                QueryTap?.Invoke(
                    arg1: canonical.Value,
                    arg2: envelope.Principal
                );
            }

            m_server.Submit(
                envelope: envelope,
                completion: result => completion(((WorldSubmissionResult.Query)result).Answer)
            );
        } else {
            completion(new QueryAnswer(
                Refused: true,
                Text: "loopback codec refused the query payload"
            ));
        }
    }
    // The tap fires before the envelope reaches the server, exactly like GrantTap/RevokeTap: replay re-applies a
    // recorded entry through this identical door (WorldServer.EnqueueAddonLifecycle), so a mount/unmount the
    // door goes on to refuse still reproduces as the identical refusal on replay rather than silently vanishing.
    /// <inheritdoc/>
    public void SubmitAddonLifecycle(WorldAddonLifecycle lifecycle, WorldPrincipal principal) =>
        SubmitTapped(
            payload: new WorldSubmissionPayload.AddonLifecycle(Value: lifecycle),
            principal: principal,
            selectValue: static payload => payload.Value,
            tap: AddonLifecycleTap
        );
    /// <inheritdoc/>
    public void SubmitCommand(WorldCommand command) {
        if (
            TryNextEnvelope(
            principal: command.Principal,
            payload: new WorldSubmissionPayload.Command(Value: command),
            envelope: out var envelope
        ) &&
            (envelope.Payload is WorldSubmissionPayload.Command canonical)
        ) {
            CommandTap?.Invoke(obj: canonical.Value);
            m_server.Submit(envelope: envelope);
        }
    }
    /// <inheritdoc/>
    public void SubmitComposition(WorldComposition composition, WorldPrincipal principal) =>
        SubmitTapped(
            payload: new WorldSubmissionPayload.Composition(Value: composition),
            principal: principal,
            selectValue: static payload => payload.Value,
            tap: CompositionTap
        );
    /// <inheritdoc/>
    public void SubmitDesignation(WorldDesignation designation, WorldPrincipal principal) =>
        SubmitTapped(
            payload: new WorldSubmissionPayload.Designation(Value: designation),
            principal: principal,
            selectValue: static payload => payload.Value,
            tap: DesignationTap
        );
    // The tap fires before the envelope reaches the server, which is where WorldGrants.Conflicts actually rules —
    // so the tape records the submitted grant, including one the door goes on to refuse. That is deliberate:
    // replay re-applies a Grant entry through this identical door (WorldReplaySnapshot.Replay calls
    // server.Grant, never a bypass), so a refusal on tape reproduces as the identical refusal on replay rather
    // than silently vanishing or silently becoming accepted.
    /// <inheritdoc/>
    public void SubmitGrant(WorldGrant grant, WorldPrincipal actor) =>
        SubmitTapped(
            payload: new WorldSubmissionPayload.Grant(Value: grant),
            principal: actor,
            selectValue: static payload => payload.Value,
            tap: GrantTap
        );
    /// <inheritdoc/>
    public void SubmitIntent(in IntentSubmission submission) {
        IntentTap?.Invoke(obj: submission);
        m_server.EnqueueIntent(submission: in submission);
    }
    /// <inheritdoc/>
    public void SubmitRebuild(WorldRebuildRequest request, WorldPrincipal principal) {
        Submit(
            principal: principal,
            payload: new WorldSubmissionPayload.Rebuild(Value: request)
        );
    }
    /// <inheritdoc/>
    public void SubmitRevoke(WorldGrant grant, WorldPrincipal actor) =>
        SubmitTapped(
            payload: new WorldSubmissionPayload.Revoke(Value: grant),
            principal: actor,
            selectValue: static payload => payload.Value,
            tap: RevokeTap
        );
    /// <inheritdoc/>
    public void SubmitScreenOp(WorldScreenOp op, WorldPrincipal principal) {
        // No transport-level tap here — unlike AddonLifecycle/Grant/Revoke, a screen op's replay-relevant content
        // hash (Insert only) is not knowable until the server reads the named path, which may only happen at apply
        // time. WorldServer.ScreenOpTap (fired from inside its own apply method, mirroring RebuildTap) is the tape's
        // capture point instead — see WorldServer.ApplyRebuild's own remarks for why this is the same shape.
        Submit(
            principal: principal,
            payload: new WorldSubmissionPayload.ScreenOp(Value: op)
        );
    }
    /// <inheritdoc/>
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
        ArgumentNullException.ThrowIfNull(argument: completion);

        if (
            TryNextEnvelope(
            principal: request.Principal,
            payload: new WorldSubmissionPayload.Session(Value: request),
            envelope: out var envelope
        ) &&
            (envelope.Payload is WorldSubmissionPayload.Session canonical)
        ) {
            SessionTap?.Invoke(obj: canonical.Value);
            m_server.Submit(
                envelope: envelope,
                completion: result => completion(((WorldSubmissionResult.Session)result).Reply)
            );
        } else {
            completion(new SessionReply(
                Accepted: false,
                AssignedIndex: -1,
                Reason: "loopback codec refused the session payload",
                RosterEcho: string.Empty
            ));
        }
    }
    /// <inheritdoc/>
    public void SubmitSessionLever(WorldSessionLever lever, WorldPrincipal principal) {
        Submit(
            principal: principal,
            payload: new WorldSubmissionPayload.Lever(Value: lever)
        );
    }
    /// <inheritdoc/>
    public void SubmitUndo(int count, WorldPrincipal principal) =>
        SubmitTapped(
            payload: new WorldSubmissionPayload.Undo(Count: count),
            principal: principal,
            selectValue: static payload => payload.Count,
            tap: UndoTap
        );
    /// <inheritdoc/>
    /// <remarks>Untapped here deliberately: a mutation is taped at the server's own envelope dispatch
    /// (<c>WorldServer.MutationTap</c>), the one ingress the loopback, an admitted socket peer, and a forwarded
    /// traveller's submission all share.</remarks>
    public void SubmitWorldMutation(WorldMutation mutation) =>
        Submit(
            payload: new WorldSubmissionPayload.Mutation(Value: mutation),
            principal: mutation.Principal
        );
}
