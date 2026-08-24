namespace Puck.World.Protocol;

/// <summary>The client→server channel: a client submits per-tick intents, authority commands, session requests, and
/// live world edits, and reads back query answers. Every non-intent submission travels as one
/// <see cref="SubmissionEnvelope"/> through the server's single ordered domain (<c>Server.WorldServer.Submit</c>) —
/// never split by kind, so a grant and the command that follows it in the same script are guaranteed to apply in
/// submission order regardless of transport. A loopback transport enqueues and drains that domain inline, on the tick
/// thread, before a <c>Submit*</c> call returns (so FIFO order and read-after-write are preserved exactly); a future
/// byte transport buffers to the same coordinate. No submission returns a value directly; every envelope
/// resolves to a typed <see cref="WorldSubmissionResult"/>, delivered to a local caller as an inline completion
/// callback (see <see cref="SubmitSession"/>/<see cref="Query"/>) and, once a wire exists, to a remote caller as a
/// Completion frame. Definition swaps and world mutations still buffer on the server and drain at the tick boundary
/// before intents (they are tick-aligned edits, not synchronous commands) — the envelope model does not change that
/// timing, only how the submission reaches it. Every fire-and-forget non-completion submission
/// (<c>Submit*</c> beside <see cref="SubmitSession"/>) is a <see cref="ServerLinkSubmissions"/> extension method over
/// <see cref="SubmitEnvelope"/> — the ONE member an implementation actually writes for all of them — rather than a
/// member of this interface: a default interface method resolves only through an <see cref="IServerLink"/>-typed
/// reference, never through a variable declared as the concrete implementing type, and callers throughout this
/// codebase hold the concrete type. An extension method resolves either way.</summary>
public interface IServerLink {
    /// <summary>Submits one entity's intent for a tick (a connection carries up to four per tick, one per local seat).</summary>
    /// <param name="submission">The tick, entity index, and merged intent.</param>
    void SubmitIntent(in IntentSubmission submission);
    /// <summary>Submits one non-completion-bearing payload behind its acting principal — the shared body every
    /// fire-and-forget <c>Submit*</c> extension method in <see cref="ServerLinkSubmissions"/> forwards to, so the two
    /// ways a submission reaches the server (loopback's local envelope/tap/apply, a federated link's remote
    /// credential/RPC) each implement this ONE member rather than ten near-identical wrappers around their own
    /// submission machinery.</summary>
    /// <param name="payload">The submission payload, already wrapped in its <see cref="WorldSubmissionPayload"/> leaf.</param>
    /// <param name="principal">The acting identity.</param>
    void SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal);
    /// <summary>Submits a session/identity request. <paramref name="completion"/> receives the server's reply (assigned
    /// index / rejection / roster echo) — for a local submitter it fires inline, before this call returns, so a
    /// caller may format its console echo entirely inside the callback with no observable difference from a
    /// synchronous return. Format every console result line from the reply the callback
    /// receives — never from a live read taken after this call returns.</summary>
    /// <param name="request">The session request.</param>
    /// <param name="completion">Invoked once with the server's reply.</param>
    void SubmitSession(SessionRequest request, Action<SessionReply> completion);
    /// <summary>Asks the server a read-back query. <paramref name="completion"/> receives the composed answer string
    /// (printed verbatim by the client) — fires inline for a local submitter, before this call returns.</summary>
    /// <param name="query">The read-back query.</param>
    /// <param name="completion">Invoked once with the composed answer.</param>
    void Query(WorldQuery query, Action<QueryAnswer> completion);
}
/// <summary>The ten fire-and-forget <see cref="IServerLink"/> submission members — every one a thin
/// <see cref="IServerLink.SubmitEnvelope"/> wrapper that differs from the next only in which
/// <see cref="WorldSubmissionPayload"/> leaf it wraps its argument in and which of its parameters is the acting
/// principal.</summary>
public static class ServerLinkSubmissions {
    /// <summary>Submits a validated authority command for one entity. Applies synchronously at submit (like a grant or
    /// a session request), so a query following it in the same script observes its effect.</summary>
    /// <param name="link">The link.</param>
    /// <param name="command">The command to apply.</param>
    public static void SubmitCommand(this IServerLink link, WorldCommand command) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.Command(Value: command),
        principal: command.Principal
    );
    /// <summary>Submits a subject-bearing write into one of a body's authored target registers. The server re-resolves
    /// the subject and applies the authored envelope before changing the register.</summary>
    /// <param name="link">The link.</param>
    /// <param name="designation">The proposed target-register write.</param>
    /// <param name="principal">The acting identity.</param>
    public static void SubmitDesignation(this IServerLink link, WorldDesignation designation, WorldPrincipal principal) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.Designation(Value: designation),
        principal: principal
    );
    /// <summary>Submits a whole-document rebuild-and-swap — <c>world.reset</c> (rebuild from the server's own base),
    /// <c>world.load</c> (rebuild from a different document), or <c>world.reload</c> (re-read the current origin from
    /// disk and rebuild from it). Buffers on the server and applies at the next step boundary: check
    /// <see cref="WorldCapability.Mutate"/> over every section (a rebuild can touch any) → (for
    /// <see cref="WorldRebuildKind.Load"/> without <see cref="WorldRebuildRequest.Force"/>) refuse while the journal
    /// is dirty → validate → swap → full derived rebuild → journal reset → re-mint every admitted peer connection's
    /// admission grant → deliver.</summary>
    /// <param name="link">The link.</param>
    /// <param name="request">The rebuild request.</param>
    /// <param name="principal">The acting identity the rebuild is checked against.</param>
    public static void SubmitRebuild(this IServerLink link, WorldRebuildRequest request, WorldPrincipal principal) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.Rebuild(Value: request),
        principal: principal
    );
    /// <summary>Submits a live world edit. Buffers on the server and drains at the next step boundary before intents:
    /// compose a candidate definition → revalidate the whole document → on failure reject loudly (definition unchanged) →
    /// on success swap the live definition, append to the journal, rebuild the changed section's derived state, and
    /// deliver the new definition to the client.</summary>
    /// <param name="link">The link.</param>
    /// <param name="mutation">The world mutation to apply.</param>
    public static void SubmitWorldMutation(this IServerLink link, WorldMutation mutation) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.Mutation(Value: mutation),
        principal: mutation.Principal
    );
    /// <summary>Requests a journal undo of the last <paramref name="count"/> applied mutations (the undo engine is
    /// replay: restore the loaded base and deterministically replay the journal minus its tail through the same apply
    /// path). Buffers on the server and drains at the next step boundary, in FIFO order with mutations and swaps. Journal
    /// control is Mutate-capability territory: the server checks <paramref name="principal"/> holds
    /// <see cref="WorldCapability.Mutate"/> over every section before it replays.</summary>
    /// <param name="link">The link.</param>
    /// <param name="count">How many trailing mutations to undo (at least 1).</param>
    /// <param name="principal">The acting identity the undo is checked against.</param>
    public static void SubmitUndo(this IServerLink link, int count, WorldPrincipal principal) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.Undo(Count: count),
        principal: principal
    );
    /// <summary>Grants a capability to a principal — the <c>world.grant</c> half. Applies synchronously at submit (like a
    /// command), so the next tick's checks observe it. <paramref name="actor"/> is the principal asking, distinct from
    /// <see cref="WorldGrant.Principal"/> (the principal receiving it); the server refuses an actor that does not itself
    /// hold <see cref="WorldGrant.Capability"/> over <see cref="WorldGrant.Subject"/> — no privilege escalation through
    /// the grant path. An exclusive grant a different principal already holds exclusively is rejected with a loud line.</summary>
    /// <param name="link">The link.</param>
    /// <param name="grant">The grant to add.</param>
    /// <param name="actor">The acting identity the grant is checked against.</param>
    public static void SubmitGrant(this IServerLink link, WorldGrant grant, WorldPrincipal actor) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.Grant(Value: grant),
        principal: actor
    );
    /// <summary>Revokes a capability from a principal — the <c>world.revoke</c> half. Applies synchronously at submit;
    /// <see cref="WorldGrant.Exclusive"/> is ignored (the subject is revoked whether or not it was exclusive).
    /// <paramref name="actor"/> is checked against the same administration rule as <see cref="SubmitGrant"/> — it must
    /// itself hold <see cref="WorldGrant.Capability"/> over <see cref="WorldGrant.Subject"/>.</summary>
    /// <param name="link">The link.</param>
    /// <param name="grant">The grant (capability + subject) to revoke.</param>
    /// <param name="actor">The acting identity the revoke is checked against.</param>
    public static void SubmitRevoke(this IServerLink link, WorldGrant grant, WorldPrincipal actor) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.Revoke(Value: grant),
        principal: actor
    );
    /// <summary>Submits a live window-composition override (<c>view.override layout</c>/<c>view.override camera</c>). Applies synchronously
    /// at submit (like a command): the server checks <paramref name="principal"/> holds <see cref="WorldCapability.Control"/>
    /// over <see cref="GrantSubject.Composition"/>, and on accept pushes it to the client's composer through
    /// <see cref="IClientSink.DeliverComposition"/>. Not durable — it never enters the document or the journal.</summary>
    /// <param name="link">The link.</param>
    /// <param name="composition">The composition override.</param>
    /// <param name="principal">The acting identity the override is checked against.</param>
    public static void SubmitComposition(this IServerLink link, WorldComposition composition, WorldPrincipal principal) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.Composition(Value: composition),
        principal: principal
    );
    /// <summary>Submits a live session lever (<c>world.volume</c>, <c>world.shadows</c>, <c>world.target</c>, …) — the
    /// same synchronous submit-and-check shape as <see cref="SubmitComposition"/>: the server checks
    /// <paramref name="principal"/> holds <see cref="WorldCapability.Mutate"/> over the section the lever folds into
    /// (<see cref="WorldSessionLever.Section"/>) and on accept pushes it to the client through
    /// <see cref="IClientSink.DeliverSessionLever"/>. Not durable — live state only, never the document or the
    /// journal.</summary>
    /// <param name="link">The link.</param>
    /// <param name="lever">The lever write.</param>
    /// <param name="principal">The acting identity the lever is checked against.</param>
    public static void SubmitSessionLever(this IServerLink link, WorldSessionLever lever, WorldPrincipal principal) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.Lever(Value: lever),
        principal: principal
    );
    /// <summary>Submits a live screen-machine lifecycle change (<c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/
    /// <c>.options</c>/<c>.link</c>/<c>.unlink</c>). Applies synchronously on the server (like
    /// <see cref="SubmitCommand"/>/<see cref="SubmitGrant"/>, never buffered to the tick boundary) — the acting
    /// identity is checked for <see cref="WorldCapability.Control"/> over the named screen (or every named screen,
    /// for a link) before <c>WorldServer.Machines</c> is touched, and the outcome is reported loudly on stderr and
    /// through <c>WorldServer.EchoTap</c>. A following <c>screen.state</c> read observes the settled state
    /// immediately, so <c>player.engage</c>'s auto-insert precheck can submit a <see cref="WorldScreenOp.Select"/>
    /// immediately ahead of the <see cref="WorldCommand.ComposeControl"/> that follows it in the same batch.</summary>
    /// <param name="link">The link.</param>
    /// <param name="op">The screen op.</param>
    /// <param name="principal">The acting identity the op is checked against.</param>
    public static void SubmitScreenOp(this IServerLink link, WorldScreenOp op, WorldPrincipal principal) => link.SubmitEnvelope(
        payload: new WorldSubmissionPayload.ScreenOp(Value: op),
        principal: principal
    );
}
