namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    private byte[]? m_pendingAddonCheckpoint;

    /// <summary>Whether attachment must restore a captured guest set instead of preparing a boot set.</summary>
    public bool HasPendingAddonCheckpoint => (m_pendingAddonCheckpoint is not null);

    internal void RequireAddonsRestored() {
        if (m_pendingAddonCheckpoint is not null) {
            throw new InvalidOperationException(message: "The checkpoint's addon state must be restored by attaching its host before stepping or capturing the authority.");
        }
    }
    // FromCheckpoint restores the authority before its host is composed. Keep the guest image until attachment;
    // a live restore, whose host is already attached, calls this at the same boundary directly.
    internal void RestoreAttachedAddons(IWorldAddonHost host) {
        if (m_pendingAddonCheckpoint is not { } image) { return; }
        using var plan = host.PrepareCheckpoint(Host.Configuration, image);

        host.Commit(plan: plan);
        host.Finish(plan: plan);
        m_pendingAddonCheckpoint = null;
    }
}
