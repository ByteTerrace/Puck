using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the document facade — the live definition, the journal and its base, the buffered live-edit
    /// ops, the compose and apply pipeline, the solid field, and the per-tick delivery decision.</summary>
    public WorldDocument Document => m_document;
    /// <summary>Gets the per-entity snapshot scratch the tick's delivery fills.</summary>
    public EntitySnapshot[] SnapshotEntries => m_tick.SnapshotEntries;
    /// <summary>Gets whether each tracked slot saw a second, different-principal write this tick.</summary>
    public bool[] TickCollided => m_tick.TickCollided;
    /// <summary>Gets the entity indices an allowed intent has already written this tick.</summary>
    public int[] TickWrittenEntity => m_tick.TickWrittenEntity;
    /// <summary>Gets the principals that wrote the matching <see cref="TickWrittenEntity"/> slot.</summary>
    public WorldPrincipal[] TickWrittenPrincipal => m_tick.TickWrittenPrincipal;

    /// <summary>Adopts a widened contention-tracking trio in one step, so the three arrays can never disagree on
    /// length.</summary>
    /// <param name="collided">The new collision flags.</param>
    /// <param name="entity">The new written-entity indices.</param>
    /// <param name="principal">The new writing principals.</param>
    public void AdoptContentionArrays(bool[] collided, int[] entity, WorldPrincipal[] principal) =>
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
    public void ApplyComposition(WorldComposition composition, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_document.ApplyComposition(
            composition: composition,
            connectionId: connectionId,
            correlationId: correlationId,
            principal: principal
        );
    /// <inheritdoc cref="WorldDocument.ApplyDesignation"/>
    public bool ApplyDesignation(WorldDesignation designation, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_document.ApplyDesignation(
            connectionId: connectionId,
            correlationId: correlationId,
            designation: designation,
            principal: principal
        );
    /// <inheritdoc cref="WorldDocument.ApplyScreenOp"/>
    public void ApplyScreenOp(WorldScreenOp op, WorldPrincipal principal, string? expectedContentHash = null) =>
        m_document.ApplyScreenOp(
            expectedContentHash: expectedContentHash,
            op: op,
            principal: principal
        );
    /// <inheritdoc cref="WorldDocument.AttachAddons"/>
    public void AttachAddons(IWorldAddonHost runtime) => m_document.AttachAddons(runtime: runtime);
    /// <inheritdoc cref="WorldDocument.AttachSink(IClientSink)"/>
    public IDisposable AttachSink(IClientSink sink) => m_document.AttachSink(sink: sink);
    /// <inheritdoc cref="WorldDocument.AttachSink(IClientSink, in WorldSinkDisclosure)"/>
    public IDisposable AttachSink(IClientSink sink, in WorldSinkDisclosure disclosure) =>
        m_document.AttachSink(
            disclosure: in disclosure,
            sink: sink
        );
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
    public void EnqueueRebuild(WorldRebuildRequest request, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, string? expectedContentHash = null) =>
        m_document.EnqueueRebuild(
            connectionId: connectionId,
            correlationId: correlationId,
            expectedContentHash: expectedContentHash,
            principal: principal,
            request: request
        );
    /// <inheritdoc cref="WorldDocument.ResolveRebuildNeighbours"/>
    public IWorldNeighbourResolver? ResolveRebuildNeighbours(string path) => m_document.ResolveRebuildNeighbours(path: path);
    /// <inheritdoc cref="WorldDocument.TryApplyMutation"/>
    public bool TryApplyMutation(WorldMutation mutation, ulong tick, ulong engineTick, int connectionId, long correlationId, bool preMetered) =>
        m_document.TryApplyMutation(
            connectionId: connectionId,
            correlationId: correlationId,
            engineTick: engineTick,
            mutation: mutation,
            preMetered: preMetered,
            tick: tick
        );
}
