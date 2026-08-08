namespace Puck.World.Protocol;

/// <summary>The in-process transport binding one client to one <see cref="IWorldServerHost"/> (a
/// <c>Puck.World.Server.WorldServer</c>, always, but this project names it only through the interface): every
/// non-intent submission (command/grant/revoke/session/rebuild/mutation/undo/composition/lever/query/addon-
/// lifecycle) travels as ONE <see cref="SubmissionEnvelope"/> through <see cref="IWorldServerHost.Submit"/> — the
/// server's single ordered domain — which this transport enqueues and drains INLINE, on the tick thread, before a
/// <c>Submit*</c> call returns (the host's command-apply window immediately precedes the tick's step, so FIFO order
/// and read-after-write are preserved — a byte transport would buffer to the same boundary instead). Per-tick
/// intents buffer separately. The produced <see cref="WorldSnapshot"/> is pushed to every attached
/// <see cref="IClientSink"/>. Single-threaded on the host tick. Every submission crosses <see cref="WorldFrameCodec"/>'s
/// canonical encode-then-decode path even when no replay is armed; loopback is a transport optimization, never a
/// second object-only protocol.</summary>
/// <remarks>Every RECORD TAP fires immediately BEFORE its write reaches the server (before <see cref="IWorldServerHost.Submit"/>
/// is called), so the tape captures the submission stream in the exact order the server saw it — including the
/// interleaving between a driving command and a grant change, which is the coordinate an authority verdict is pinned
/// against. <see cref="IntentTap"/>/<see cref="CommandTap"/>/<see cref="GrantTap"/>/<see cref="RevokeTap"/>/
/// <see cref="SessionTap"/>/<see cref="AddonLifecycleTap"/> are captured on tape today; the envelope/ordered-domain
/// reshape does not add or remove tape coverage beyond that.</remarks>
public sealed class LoopbackTransport : IServerLink {
    private readonly IWorldServerHost m_server;
    // The local connection's per-connection monotonic Sequence and the CorrelationId every envelope mints — both
    // simple auto-incrementing counters today (nothing consults Sequence for backpressure over loopback, and nothing
    // correlates a remote reply against CorrelationId yet); the wire transport will need real ones, this one just has
    // to be MONOTONIC so the envelope shape is honest.
    private long m_sequence;
    private long m_correlationId;

    /// <summary>Initializes a new instance of the <see cref="LoopbackTransport"/> class over the server it fronts.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public LoopbackTransport(IWorldServerHost server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        m_server = server;
    }

    /// <summary>Gets an optional record tap invoked with every submitted intent before it reaches the server — the seam the
    /// replay tape captures the live per-tick intent stream through. <see langword="null"/> (the default) is a free
    /// pass-through; set only while a recording is armed.</summary>
    public Action<IntentSubmission>? IntentTap { get; set; }

    /// <summary>Gets an optional record tap invoked with every authority command before it applies — one kind of entry in
    /// the captured per-tick authority stream. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldCommand>? CommandTap { get; set; }

    /// <summary>Gets an optional record tap invoked with every designation before it applies.</summary>
    public Action<WorldDesignation, WorldPrincipal>? DesignationTap { get; set; }

    /// <summary>Gets an optional record tap invoked with every grant acquisition before it applies, carrying the grant row
    /// and the ACTOR that asked for it. Captured for the same reason commands are: authority is an input to the tick,
    /// and a replay whose fresh world was never granted re-drives a differently-authorized simulation — which bites
    /// hardest on the addon path, where the re-run guest is checked against the replayed world's own table.
    /// <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldGrant, WorldPrincipal>? GrantTap { get; set; }

    /// <summary>Gets an optional record tap invoked with every revocation before it applies — the mirror of
    /// <see cref="GrantTap"/>. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldGrant, WorldPrincipal>? RevokeTap { get; set; }

    /// <summary>Gets an optional record tap invoked with every session request before it applies. Occupancy, profile, and
    /// population changes are authoritative inputs to later simulation ticks.</summary>
    public Action<SessionRequest>? SessionTap { get; set; }

    /// <summary>Gets an optional record tap invoked with every addon-lifecycle submission before it reaches the server,
    /// carrying the action and the ACTOR that submitted it — the same reasoning <see cref="GrantTap"/> carries: a
    /// replay whose fresh world never re-mounted (or re-unmounted) a guest re-drives a differently-composed
    /// simulation. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldAddonLifecycle, WorldPrincipal>? AddonLifecycleTap { get; set; }

    /// <summary>Binds the client sink the server delivers each tick's snapshot to.</summary>
    /// <param name="sink">The client sink.</param>
    public void Bind(IClientSink sink) {
        m_server.AttachSink(sink: sink);
    }

    /// <inheritdoc/>
    public void SubmitIntent(in IntentSubmission submission) {
        IntentTap?.Invoke(obj: submission);
        m_server.EnqueueIntent(submission: in submission);
    }

    /// <inheritdoc/>
    public void SubmitCommand(WorldCommand command) {
        if (TryNextEnvelope(principal: command.Principal, payload: new WorldSubmissionPayload.Command(Value: command), envelope: out var envelope) &&
            (envelope.Payload is WorldSubmissionPayload.Command canonical)) {
            CommandTap?.Invoke(obj: canonical.Value);
            m_server.Submit(envelope: envelope);
        }
    }

    /// <inheritdoc/>
    public void SubmitDesignation(WorldDesignation designation, WorldPrincipal principal) {
        if (TryNextEnvelope(principal: principal, payload: new WorldSubmissionPayload.Designation(Value: designation), envelope: out var envelope)
            && (envelope.Payload is WorldSubmissionPayload.Designation canonical)) {
            DesignationTap?.Invoke(arg1: canonical.Value, arg2: envelope.Principal);
            m_server.Submit(envelope: envelope);
        }
    }

    /// <inheritdoc/>
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
        ArgumentNullException.ThrowIfNull(argument: completion);

        if (TryNextEnvelope(principal: request.Principal, payload: new WorldSubmissionPayload.Session(Value: request), envelope: out var envelope) &&
            (envelope.Payload is WorldSubmissionPayload.Session canonical)) {
            SessionTap?.Invoke(obj: canonical.Value);
            m_server.Submit(envelope: envelope, completion: result => completion(((WorldSubmissionResult.Session)result).Reply));
        } else {
            completion(new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: "loopback codec refused the session payload"));
        }
    }

    /// <inheritdoc/>
    public void Query(WorldQuery query, Action<QueryAnswer> completion) {
        ArgumentNullException.ThrowIfNull(argument: completion);

        // Queries carry no principal of their own today (every caller is an in-process, trusted console/script
        // reader — see WorldCapability.Observe's own remarks); Console is the honest stand-in until a wire admits
        // untrusted queries and this transport starts stamping the connection's own claimed identity instead.
        if (TryNextEnvelope(principal: WorldPrincipal.Console, payload: new WorldSubmissionPayload.Query(Value: query), envelope: out var envelope)) {
            m_server.Submit(envelope: envelope, completion: result => completion(((WorldSubmissionResult.Query)result).Answer));
        } else {
            completion(new QueryAnswer(Text: "loopback codec refused the query payload", Refused: true));
        }
    }

    /// <inheritdoc/>
    public void SubmitRebuild(WorldRebuildRequest request, WorldPrincipal principal) {
        Submit(principal: principal, payload: new WorldSubmissionPayload.Rebuild(Value: request));
    }

    /// <inheritdoc/>
    public void SubmitWorldMutation(WorldMutation mutation) {
        Submit(principal: mutation.Principal, payload: new WorldSubmissionPayload.Mutation(Value: mutation));
    }

    /// <inheritdoc/>
    public void SubmitUndo(int count, WorldPrincipal principal) {
        Submit(principal: principal, payload: new WorldSubmissionPayload.Undo(Count: count));
    }

    /// <inheritdoc/>
    public void SubmitGrant(WorldGrant grant, WorldPrincipal actor) {
        // The tap fires BEFORE the envelope reaches the server, which is where WorldGrants.Conflicts actually rules —
        // so the tape records the SUBMITTED grant, including one the door goes on to refuse. That is deliberate:
        // replay re-applies a Grant entry through this identical door (WorldReplaySnapshot.Replay calls
        // server.Grant, never a bypass), so a refusal on tape reproduces as the identical refusal on replay rather
        // than silently vanishing or silently becoming accepted.
        if (TryNextEnvelope(principal: actor, payload: new WorldSubmissionPayload.Grant(Value: grant), envelope: out var envelope) &&
            (envelope.Payload is WorldSubmissionPayload.Grant canonical)) {
            GrantTap?.Invoke(arg1: canonical.Value, arg2: envelope.Principal);
            m_server.Submit(envelope: envelope);
        }
    }

    /// <inheritdoc/>
    public void SubmitRevoke(WorldGrant grant, WorldPrincipal actor) {
        if (TryNextEnvelope(principal: actor, payload: new WorldSubmissionPayload.Revoke(Value: grant), envelope: out var envelope) &&
            (envelope.Payload is WorldSubmissionPayload.Revoke canonical)) {
            RevokeTap?.Invoke(arg1: canonical.Value, arg2: envelope.Principal);
            m_server.Submit(envelope: envelope);
        }
    }

    /// <inheritdoc/>
    public void SubmitComposition(WorldComposition composition, WorldPrincipal principal) {
        Submit(principal: principal, payload: new WorldSubmissionPayload.Composition(Value: composition));
    }

    /// <inheritdoc/>
    public void SubmitSessionLever(WorldSessionLever lever, WorldPrincipal principal) {
        Submit(principal: principal, payload: new WorldSubmissionPayload.Lever(Value: lever));
    }

    /// <inheritdoc/>
    public void SubmitAddonLifecycle(WorldAddonLifecycle lifecycle, WorldPrincipal principal) {
        // The tap fires BEFORE the envelope reaches the server, exactly like GrantTap/RevokeTap: replay re-applies a
        // recorded entry through this identical door (WorldServer.EnqueueAddonLifecycle), so a mount/unmount the
        // door goes on to refuse still reproduces as the identical refusal on replay rather than silently vanishing.
        if (TryNextEnvelope(principal: principal, payload: new WorldSubmissionPayload.AddonLifecycle(Value: lifecycle), envelope: out var envelope) &&
            (envelope.Payload is WorldSubmissionPayload.AddonLifecycle canonical)) {
            AddonLifecycleTap?.Invoke(arg1: canonical.Value, arg2: envelope.Principal);
            m_server.Submit(envelope: envelope);
        }
    }

    /// <inheritdoc/>
    public void SubmitScreenOp(WorldScreenOp op, WorldPrincipal principal) {
        // No transport-level tap here — unlike AddonLifecycle/Grant/Revoke, a screen op's replay-relevant content
        // hash (Insert only) is not knowable until the server reads the named path, which may only happen at apply
        // time. WorldServer.ScreenOpTap (fired from inside its own apply method, mirroring RebuildTap) is the tape's
        // capture point instead — see WorldServer.ApplyRebuild's own remarks for why this is the same shape.
        Submit(principal: principal, payload: new WorldSubmissionPayload.ScreenOp(Value: op));
    }

    // Mints the next envelope for the LOCAL connection (id 0, generation 0) — Sequence/CorrelationId both simple
    // monotonic counters (see their own field remarks).
    private void Submit(WorldPrincipal principal, WorldSubmissionPayload payload) {
        if (TryNextEnvelope(principal: principal, payload: payload, envelope: out var envelope)) {
            m_server.Submit(envelope: envelope);
        }
    }

    // The ALWAYS-BYTES rule: even the in-process link is defined by the same canonical frame a future socket carries.
    // A refusal is a transport verdict printed by name; invalid caller state never escapes as an invariant exception.
    private bool TryNextEnvelope(WorldPrincipal principal, WorldSubmissionPayload payload, out SubmissionEnvelope envelope) {
        if (!WorldFrameCodec.TryEncode(payload: payload, frame: out var frame, failure: out var failure) ||
            !WorldFrameCodec.TryDecode(frame: frame, payload: out var decoded, failure: out failure) ||
            (decoded is null)) {
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
}
