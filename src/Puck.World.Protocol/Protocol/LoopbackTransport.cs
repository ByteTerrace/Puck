using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>The in-process transport binding one client to one <see cref="IWorldServerHost"/> (a
/// <c>Puck.World.Server.WorldServer</c>, always, but this project names it only through the interface).</summary>
/// <remarks>
/// Every non-intent submission (command/grant/revoke/session/rebuild/mutation/undo/composition/lever/query/screen-
/// op/designation) travels as one <see cref="SubmissionEnvelope"/> through <see cref="IWorldServerHost.Submit"/> — the
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

    /// <summary>Gets an optional record tap invoked with every authority command before it applies — one kind of entry in
    /// the captured per-tick authority stream. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldCommand>? CommandTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every window-composition submission before it reaches the
    /// server, carrying the composition and its actor. <see langword="null"/> (the default) is a free
    /// pass-through.</summary>
    public Action<WorldComposition, Principal>? CompositionTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every designation before it applies.</summary>
    public Action<WorldDesignation, Principal>? DesignationTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every grant acquisition before it applies, carrying the grant row
    /// and the actor that asked for it. Captured for the same reason commands are: authority is an input to the tick,
    /// and a replay whose fresh world was never granted re-drives a differently-authorized simulation — which bites
    /// hardest on the addon path, where the re-run guest is checked against the replayed world's own table.
    /// <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldGrant, Principal>? GrantTap { get; set; }
    /// <summary>Gets or sets a value indicating whether the local connection's driving input — every
    /// <see cref="SubmitIntent"/> and every <see cref="WorldSubmissionPayload.Command"/> — is dropped before it
    /// reaches a tap or the server. A replay drive holds this while it feeds a tape's recorded intents and commands
    /// through the server's own doors, so a device stick or a <c>body.*</c> verb cannot fold into the re-driven
    /// tick; every other payload kind (grant, session, mutation, …) still crosses.</summary>
    public bool InputMasked { get; set; }
    /// <summary>Gets or sets where this connection mints the operation id of a mutation whose caller named none:
    /// <see cref="WorldOperationIds.Random"/> unless a harness seeds it so paired runs submit identical envelopes.</summary>
    public WorldOperationIds OperationIds { get; set; } = WorldOperationIds.Random;
    /// <summary>Gets an optional record tap invoked with every submitted intent before it reaches the server — the seam the
    /// replay tape captures the live per-tick intent stream through. <see langword="null"/> (the default) is a free
    /// pass-through; set only while a recording is armed.</summary>
    public Action<IntentSubmission>? IntentTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every read-back query before it reaches the server, carrying
    /// the query and the identity the envelope stamped. Captured because a query crosses the same Observe gate a
    /// grant change moves, so a replay that skipped it would exercise a different admission history.
    /// <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldQuery, Principal>? QueryTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every revocation before it applies — the mirror of
    /// <see cref="GrantTap"/>. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldGrant, Principal>? RevokeTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every session request before it applies. Occupancy, profile, and
    /// population changes are authoritative inputs to later simulation ticks.</summary>
    public Action<SessionRequest>? SessionTap { get; set; }
    /// <summary>Gets an optional record tap invoked with every journal undo or redo before it reaches the server, carrying
    /// the request and its actor. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldSubmissionPayload.Undo, Principal>? UndoTap { get; set; }

    // Only canonical payloads reach replay taps and the ordered submission domain.
    private long Mint(WorldSubmissionPayload payload, Principal principal, Guid operationId, Action<WorldSubmissionResult>? completion, WorldDeferredVerbRow? console) {
        if ((payload is WorldSubmissionPayload.Command) && InputMasked) { return 0; }
        if ((payload is WorldSubmissionPayload.Mutation) && (operationId == Guid.Empty)) {
            completion?.Invoke(new WorldSubmissionResult.Refusal(
                Code: "world.mutation.operation-id-missing",
                Detail: "mutation operation id is required"
            ));
            return 0;
        }
        if (!WorldFrameCodec.TryEncode(payload, operationId, out var frame, out var failure) ||
            !WorldFrameCodec.TryDecode(frame, out var canonical, out var decodedId, out failure) ||
            (canonical is null)) {
            if (completion is null) {
                Console.Error.WriteLine($"[world.codec refused: {failure}]");
                console?.RefusedCodec();
            } else {
                completion(new WorldSubmissionResult.Refusal(
                    Code: "world.transport.codec-refused",
                    Detail: $"the submission could not be encoded or decoded: {failure}"
                ));
            }
            return 0;
        }

        var envelope = new SubmissionEnvelope(
            ConnectionId: SubmissionEnvelope.LocalConnectionId,
            SessionGeneration: 0,
            Sequence: ++m_sequence,
            CorrelationId: ++m_correlationId,
            Principal: principal,
            Payload: canonical,
            OperationId: decodedId
        );

        Tap(envelope);
        console?.Minted(correlationId: envelope.CorrelationId, payload: canonical);
        try {
            m_server.Submit(completion: completion, envelope: envelope);
        } finally {
            console?.Submitted(correlationId: envelope.CorrelationId, payload: canonical);
        }

        return envelope.CorrelationId;
    }

    // Record the submitted values before the authority decides their verdict. Mutation and screen op taps run
    // on the server, which also sees those submissions from socket peers and forwarded travelers.
    private void Tap(in SubmissionEnvelope envelope) {
        switch (envelope.Payload) {
            case WorldSubmissionPayload.Command command: CommandTap?.Invoke(command.Value); break;
            case WorldSubmissionPayload.Composition composition: CompositionTap?.Invoke(composition.Value, envelope.Principal); break;
            case WorldSubmissionPayload.Designation designation: DesignationTap?.Invoke(designation.Value, envelope.Principal); break;
            case WorldSubmissionPayload.Grant grant: GrantTap?.Invoke(grant.Value, envelope.Principal); break;
            case WorldSubmissionPayload.Revoke revoke: RevokeTap?.Invoke(revoke.Value, envelope.Principal); break;
            case WorldSubmissionPayload.Undo undo: UndoTap?.Invoke(undo, envelope.Principal); break;
            case WorldSubmissionPayload.Query query: QueryTap?.Invoke(query.Value, envelope.Principal); break;
            case WorldSubmissionPayload.Session session: SessionTap?.Invoke(session.Value); break;
        }
    }

    /// <inheritdoc/>
    public Guid NextOperationId(Principal principal) => OperationIds.Next(principal: principal);
    /// <summary>Binds the client sink the server delivers each tick's snapshot to.</summary>
    /// <param name="sink">The client sink.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed — see
    /// <see cref="IWorldServerHost.AttachSink"/>'s own remarks for the threading and disposal contract.</returns>
    public IDisposable Bind(IClientSink sink) {
        return m_server.AttachSink(sink: sink);
    }
    /// <inheritdoc/>
    public void Query(WorldQuery query, Principal principal, Action<QueryAnswer> completion) {
        ArgumentNullException.ThrowIfNull(argument: completion);

        // The envelope stamps the identity used for Observe and session admission.
        _ = Mint(new WorldSubmissionPayload.Query(query), principal, Guid.Empty, result => completion(result switch {
            WorldSubmissionResult.Query queryResult => queryResult.Answer,
            WorldSubmissionResult.Refusal refusal => new QueryAnswer(Text: refusal.Detail, Refused: true),
            _ => throw new InvalidOperationException("unexpected loopback query completion"),
        }), console: null);
    }
    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => SubmitEnvelope(
        completion: null,
        operationId: Guid.Empty,
        payload: payload,
        principal: principal
    );
    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId) => SubmitEnvelope(
        completion: null,
        operationId: operationId,
        payload: payload,
        principal: principal
    );
    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId, Action<WorldSubmissionResult>? completion) => SubmitEnvelope(
        completion: completion,
        console: null,
        operationId: operationId,
        payload: payload,
        principal: principal
    );
    /// <summary>Returns the link a console submits this row's lines through: each submission it mints registers in
    /// <paramref name="row"/>'s table, so its verdict answers and counts; this bare transport registers nothing.</summary>
    /// <param name="row">The row's handle on the console's table.</param>
    /// <returns>The console link, created once per row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public WorldConsoleServerLink ForConsole(WorldDeferredVerbRow row) {
        ArgumentNullException.ThrowIfNull(argument: row);

        return new WorldConsoleServerLink(
            row: row,
            transport: this
        );
    }

    internal long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId, Action<WorldSubmissionResult>? completion, WorldDeferredVerbRow? console) =>
        Mint(payload, principal, operationId, completion, console);

    /// <inheritdoc/>
    public void SubmitIntent(in IntentSubmission submission) {
        if (InputMasked) {
            return;
        }

        IntentTap?.Invoke(obj: submission);
        m_server.EnqueueIntent(submission: in submission);
    }
    /// <inheritdoc/>
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
        ArgumentNullException.ThrowIfNull(argument: completion);

        _ = Mint(new WorldSubmissionPayload.Session(request), request.Principal, Guid.Empty, result => completion(result switch {
            WorldSubmissionResult.Session session => session.Reply,
            WorldSubmissionResult.Refusal refusal => new SessionReply(Accepted: false, AssignedIndex: -1, Reason: refusal.Detail, RosterEcho: string.Empty),
            _ => throw new InvalidOperationException("unexpected loopback session completion"),
        }), console: null);
    }
}
