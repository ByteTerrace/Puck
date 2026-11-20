using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the document facade — the live definition, the journal and its base, the buffered live-edit
    /// ops, the compose and apply pipeline, the solid field, and the per-tick delivery decision.</summary>
    public WorldDocument Document { get { ThrowIfTickPending(); return m_document; } }
    /// <summary>Gets the per-entity snapshot scratch the tick's delivery fills.</summary>
    internal EntitySnapshot[] SnapshotEntries => m_tick.SnapshotEntries;
    /// <summary>Gets the entity indices an allowed intent has already written this tick.</summary>
    public int[] TickWrittenEntity => m_tick.TickWrittenEntity;

    /// <summary>Adopts a widened contention-tracking trio in one step, so the three arrays can never disagree on
    /// length.</summary>
    /// <param name="collided">The new collision flags.</param>
    /// <param name="entity">The new written-entity indices.</param>
    /// <param name="principal">The new writing principals.</param>
    public void AdoptContentionArrays(bool[] collided, int[] entity, Principal[] principal) =>
        m_tick.AdoptContentionArrays(
            collided: collided,
            entity: entity,
            principal: principal
        );
    /// <summary>Drops every placement-deal memo and the definition the sweep last ran over, so the next sweep
    /// re-derives from the installed document.</summary>
    public void ResetPlacementDeals() => m_tick.ResetPlacementDeals();
    /// <summary>Adopts the mounted Simulation-lane addon host.</summary>
    /// <param name="runtime">The host to adopt.</param>
    public void AdoptAddonHost(IWorldAddonHost runtime) => m_addons = runtime;
    /// <summary>Latches that a screen operation reached host dispatch, closing boot-only replay and checkpoint
    /// reconstruction.</summary>
    public void NoteScreenOpApplied() => AnyScreenOpEverApplied = true;
    /// <inheritdoc cref="WorldDocument.ApplyCommand"/>
    public void ApplyCommand(WorldCommand command, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_document.ApplyCommand(
            command: command,
            connectionId: connectionId,
            correlationId: correlationId
        );
    /// <inheritdoc cref="WorldDocument.ApplyComposition"/>
    public void ApplyComposition(WorldComposition composition, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_document.ApplyComposition(
            composition: composition,
            connectionId: connectionId,
            correlationId: correlationId,
            principal: principal
        );
    /// <inheritdoc cref="WorldDocument.ApplyDesignation"/>
    public bool ApplyDesignation(WorldDesignation designation, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_document.ApplyDesignation(
            connectionId: connectionId,
            correlationId: correlationId,
            designation: designation,
            principal: principal
        );
    /// <inheritdoc cref="WorldDocument.ApplyScreenOp"/>
    public void ApplyScreenOp(WorldScreenOp op, Principal principal) =>
        m_document.ApplyScreenOp(
            op: op,
            principal: principal
        );
    /// <inheritdoc cref="WorldDocument.AttachAddons"/>
    public void AttachAddons(IWorldAddonHost runtime, WorldDocumentInputs? preparedFrom = null) => m_document.AttachAddons(preparedFrom: preparedFrom, runtime: runtime);
    /// <inheritdoc cref="WorldDocument.AttachSink(IClientSink)"/>
    public IDisposable AttachSink(IClientSink sink) => ExecuteAuthorityOperation(() => m_document.AttachSink(sink: sink));

    /// <summary>Attaches the in-process presentation that serves every local seat. Bodies and the document arrive
    /// whole; each cue is admitted per seat through that seat's live read capability and carries the seats that may
    /// be told it (<see cref="WorldGameplayCue.Audience"/>).</summary>
    /// <param name="sink">The local presentation's sink.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed.</returns>
    public IDisposable AttachLocalPresentation(IClientSink sink) => ExecuteAuthorityOperation(() => {
        WorldRecipientView[] views = [];
        return m_document.AttachSink(sink, WorldSinkDisclosure.Full, snapshot => {
            var seats = Math.Min(Definition.Population.LocalSeats, WorldGameplayCue.MaxAudienceSeats);
            if (views.Length != seats) {
                views = new WorldRecipientView[seats];
                for (var slot = 0; slot < seats; slot++) { views[slot] = DeliveryView(Principal.Seat(slot), WorldDisclosureTier.Replica); }
            }
            return Tick.PresentLocalCues(snapshot, views);
        });
    });
    /// <inheritdoc cref="WorldDocument.EnqueueMutation"/>
    public void EnqueueMutation(WorldMutation mutation, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, long sourceAddonInstanceId = -1L, ushort actOrdinal = 0, Action<bool>? outcomeObserved = null, WorldMutationBinding? binding = null, Action<WorldSubmissionResult>? completion = null) =>
        m_document.EnqueueMutation(
            actOrdinal: actOrdinal,
            binding: binding,
            completion: completion,
            connectionId: connectionId,
            correlationId: correlationId,
            mutation: mutation,
            outcomeObserved: outcomeObserved,
            sourceAddonInstanceId: sourceAddonInstanceId
        );
    /// <inheritdoc cref="WorldDocument.EnqueueRebuild"/>
    public void EnqueueRebuild(WorldRebuildRequest request, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, string? expectedContentHash = null) =>
        ExecuteAuthorityOperation(() => m_document.EnqueueRebuild(
            connectionId: connectionId,
            correlationId: correlationId,
            expectedContentHash: expectedContentHash,
            principal: principal,
            request: request
        ));
    /// <summary>Gets a task that completes once every rebuild this authority has started preparing off its tick has
    /// finished preparing. A prepared rebuild installs at the next drain; nothing on the tick waits for it.</summary>
    public Task RebuildPreparations => ExecuteAuthorityOperation(operation: () => m_document.RebuildPreparations);
    /// <inheritdoc cref="WorldDocument.ResolveRebuildNeighbours"/>
    public IWorldNeighbourResolver? ResolveRebuildNeighbours(string path) => m_document.ResolveRebuildNeighbours(path: path);
    /// <inheritdoc cref="WorldDocument.TryApplyMutation"/>
    [Replaces(root: typeof(StateArena))]
    public bool TryApplyMutation(WorldMutation mutation, ulong tick, ulong engineTick, int connectionId, long correlationId, bool preMetered, bool derived = false) =>
        m_document.TryApplyMutation(
            connectionId: connectionId,
            correlationId: correlationId,
            derived: derived,
            engineTick: engineTick,
            mutation: mutation,
            preMetered: preMetered,
            tick: tick
        );
}
