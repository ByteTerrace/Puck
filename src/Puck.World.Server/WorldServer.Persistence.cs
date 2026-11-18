using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the persistence facade — journal undo and its depth horizon, the authority checkpoint's
    /// capture and restore, the simulation state hash, and the replay timeline gate.</summary>
    public WorldPersistence Persistence => m_persistence;

    /// <inheritdoc cref="WorldPersistence.FromCheckpoint"/>
    [Replaces(root: typeof(StateArena))]
    public static (WorldServer Server, WorldPopulation Population) FromCheckpoint(WorldAuthorityCheckpoint checkpoint, WorldOwnedWorlds profiles, IWorldMachineHost machines, string instanceIdentity, IWorldAdjacencySource? adjacencies = null, string? documentDirectory = null, bool restoreOwnedIdentities = true, string? authorityIdentity = null, WorldPrivateDrawKeys? drawKeys = null) =>
        WorldPersistence.FromCheckpoint(
            drawKeys: drawKeys,
            adjacencies: adjacencies,
            authorityIdentity: authorityIdentity,
            checkpoint: checkpoint,
            documentDirectory: documentDirectory,
            instanceIdentity: instanceIdentity,
            machines: machines,
            profiles: profiles,
            restoreOwnedIdentities: restoreOwnedIdentities
        );
    /// <inheritdoc cref="WorldPersistence.EnforceJournalDepth"/>
    public void EnforceJournalDepth() => m_persistence.EnforceJournalDepth();
    /// <inheritdoc cref="WorldPersistence.EnqueueUndo"/>
    public void EnqueueUndo(int count, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_persistence.EnqueueUndo(
            connectionId: connectionId,
            correlationId: correlationId,
            count: count,
            principal: principal
        );
    /// <inheritdoc cref="WorldPersistence.RestoreCheckpoint"/>
    [Replaces(root: typeof(StateArena))]
    public void RestoreCheckpoint(WorldAuthorityCheckpoint checkpoint) => m_persistence.RestoreCheckpoint(checkpoint: checkpoint);
    /// <inheritdoc cref="WorldPersistence.TryApplyJournalTailMutation"/>
    [Replaces(root: typeof(StateArena))]
    public bool TryApplyJournalTailMutation(WorldMutation mutation, ulong tick, ulong engineTick) =>
        m_persistence.TryApplyJournalTailMutation(
            engineTick: engineTick,
            mutation: mutation,
            tick: tick
        );
    /// <summary>Replays one durable mutation with the authority inputs consumed before the last activation ended.</summary>
    [Replaces(root: typeof(StateArena))]
    public bool TryRecoverJournalMutation(WorldJournalEntry entry, WorldPrivateDrawKeys authority) {
        RequireAuthorityCredential(authority);
        using var input = DrawKeys.Replay(entry.DrawHistory?.Words ?? [], static address => ReplayRefusal.MissingPrivateDraw.Raise(address));
        if (entry.DrawHistory is { } history) {
            for (var i = 0; i < Arena.Rows.Count; i++) {
                if (Arena.Rows[i].Draw?.Secret is { } site && history.Positions.TryGetValue(site.Installation, out var position)
                    && !ArenaDraws.TryWrite(Arena, i, position.Cursor, position.Masks, out _)) { return false; }
            }
        }
        return TryApplyJournalTailMutation(entry.Mutation, entry.Tick, entry.EngineTick);
    }

    /// <inheritdoc cref="WorldPersistence.TryCaptureCheckpoint"/>
    public bool TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint hostRow, out WorldAuthorityCheckpoint? checkpoint, out string reason) =>
        m_persistence.TryCaptureCheckpoint(
            checkpoint: out checkpoint,
            hostRow: hostRow,
            reason: out reason
        );
}
