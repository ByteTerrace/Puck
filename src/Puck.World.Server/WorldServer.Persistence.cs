using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the persistence facade — journal undo and its depth horizon, the authority checkpoint's
    /// capture and restore, the simulation state hash, and the replay timeline gate.</summary>
    public WorldPersistence Persistence => m_persistence;

    /// <inheritdoc cref="WorldPersistence.FromCheckpoint"/>
    public static (WorldServer Server, WorldPopulation Population) FromCheckpoint(WorldAuthorityCheckpoint checkpoint, WorldOwnedWorlds profiles, IWorldMachineHost machines, string instanceIdentity) =>
        WorldPersistence.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: instanceIdentity,
            machines: machines,
            profiles: profiles
        );
    /// <inheritdoc cref="WorldPersistence.EnforceJournalDepth"/>
    public void EnforceJournalDepth() => m_persistence.EnforceJournalDepth();
    /// <inheritdoc cref="WorldPersistence.EnqueueUndo"/>
    public void EnqueueUndo(int count, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_persistence.EnqueueUndo(
            connectionId: connectionId,
            correlationId: correlationId,
            count: count,
            principal: principal
        );
    /// <inheritdoc cref="WorldPersistence.RestoreCheckpoint"/>
    public void RestoreCheckpoint(WorldAuthorityCheckpoint checkpoint) => m_persistence.RestoreCheckpoint(checkpoint: checkpoint);
    /// <inheritdoc cref="WorldPersistence.TryApplyJournalTailMutation"/>
    public bool TryApplyJournalTailMutation(WorldMutation mutation, ulong tick, ulong engineTick) =>
        m_persistence.TryApplyJournalTailMutation(
            engineTick: engineTick,
            mutation: mutation,
            tick: tick
        );
    /// <inheritdoc cref="WorldPersistence.TryCaptureCheckpoint"/>
    public bool TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint hostRow, out WorldAuthorityCheckpoint? checkpoint, out string reason) =>
        m_persistence.TryCaptureCheckpoint(
            checkpoint: out checkpoint,
            hostRow: hostRow,
            reason: out reason
        );
}
