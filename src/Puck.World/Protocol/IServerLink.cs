namespace Puck.World.Protocol;

/// <summary>The client→server channel: a client submits per-tick intents, authority commands, session requests, and
/// live world edits, and reads back query answers. A loopback transport applies (or buffers) submissions at the
/// server's next step boundary; a future byte transport serializes them. Definition swaps and world mutations BUFFER on
/// the server and drain at the tick boundary before intents (they are tick-aligned edits, not synchronous commands).</summary>
internal interface IServerLink {
    /// <summary>Submits one entity's intent for a tick (a connection carries up to four per tick, one per local seat).</summary>
    /// <param name="submission">The tick, entity index, and merged intent.</param>
    void SubmitIntent(in IntentSubmission submission);

    /// <summary>Submits a validated authority command for one entity, applied at the server's next step boundary.</summary>
    /// <param name="command">The command to apply.</param>
    void SubmitCommand(WorldCommand command);

    /// <summary>Submits a session/identity request and returns the server's reply (assigned index / rejection / roster echo).</summary>
    /// <param name="request">The session request.</param>
    /// <returns>The server's reply.</returns>
    SessionReply SubmitSession(SessionRequest request);

    /// <summary>Asks the server a read-back query and returns its composed answer string, printed verbatim by the client.</summary>
    /// <param name="query">The read-back query.</param>
    /// <returns>The composed answer.</returns>
    QueryAnswer Query(WorldQuery query);

    /// <summary>Submits a whole new world definition — the whole-document swap (load a different world file at runtime;
    /// the editor's revert/load). BUFFERS on the server and applies at the next step boundary: check
    /// <see cref="WorldCapability.Mutate"/> over EVERY section (a swap can touch any) → validate → swap → full derived
    /// rebuild → journal RESET (the loaded definition becomes the new base) → deliver.</summary>
    /// <param name="definition">The world definition to install.</param>
    /// <param name="principal">The acting identity the swap is checked against.</param>
    void SubmitDefinition(WorldDefinition definition, WorldPrincipal principal);

    /// <summary>Submits a live world edit. BUFFERS on the server and drains at the next step boundary before intents:
    /// compose a candidate definition → revalidate the whole document → on failure reject loudly (definition unchanged) →
    /// on success swap the live definition, append to the journal, rebuild the changed section's derived state, and
    /// deliver the new definition to the client.</summary>
    /// <param name="mutation">The world mutation to apply.</param>
    void SubmitWorldMutation(WorldMutation mutation);

    /// <summary>Requests a journal undo of the last <paramref name="count"/> applied mutations (the undo engine is
    /// replay: restore the loaded base and deterministically replay the journal minus its tail through the same apply
    /// path). BUFFERS on the server and drains at the next step boundary, in FIFO order with mutations and swaps. Journal
    /// control is Mutate-capability territory: the server checks <paramref name="principal"/> holds
    /// <see cref="WorldCapability.Mutate"/> over EVERY section before it replays.</summary>
    /// <param name="count">How many trailing mutations to undo (at least 1).</param>
    /// <param name="principal">The acting identity the undo is checked against.</param>
    void SubmitUndo(int count, WorldPrincipal principal);

    /// <summary>Grants a capability to a principal — the <c>world.grant</c> half. Applies SYNCHRONOUSLY at submit (like a
    /// command), so the next tick's checks observe it. An exclusive grant a DIFFERENT principal already holds
    /// exclusively is rejected with a loud line.</summary>
    /// <param name="grant">The grant to add.</param>
    void SubmitGrant(WorldGrant grant);

    /// <summary>Revokes a capability from a principal — the <c>world.revoke</c> half. Applies SYNCHRONOUSLY at submit;
    /// <see cref="WorldGrant.Exclusive"/> is ignored (the subject is revoked whether or not it was exclusive).</summary>
    /// <param name="grant">The grant (capability + subject) to revoke.</param>
    void SubmitRevoke(WorldGrant grant);
}
