namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    private byte[]? m_pendingAddonCheckpoint;

    internal void RequireAddonsRestored() {
        if (m_pendingAddonCheckpoint is not null) {
            throw new InvalidOperationException(message: "The checkpoint's addon state must be restored by attaching its host before stepping or capturing the authority.");
        }
    }
    // FromCheckpoint restores the authority before its host is composed. Keep the guest image until attachment;
    // a live restore, whose host is already attached, calls this at the same boundary directly.
    internal void RestoreAttachedAddons(IWorldAddonHost host) {
        if (m_pendingAddonCheckpoint is not { } image) { return; }
        if (host is not IWorldAddonCheckpointHost checkpointHost) {
            throw new InvalidOperationException(message: "Checkpoint contains addon state that this host cannot restore.");
        }
        checkpointHost.RestoreCheckpoint(checkpoint: image);
        m_pendingAddonCheckpoint = null;
    }
}
