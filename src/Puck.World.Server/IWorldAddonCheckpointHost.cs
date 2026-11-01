namespace Puck.World.Server;

/// <summary>The optional durable state boundary of a mounted addon host. The authority codec frames the opaque
/// image just as it frames machine runtime images; a host without this capability refuses pumped-state capture.</summary>
public interface IWorldAddonCheckpointHost {
    /// <summary>Captures every mounted guest and its cross-tick host state, or throws with the unsupported resource's name.</summary>
    /// <returns>The complete addon image.</returns>
    byte[] CaptureCheckpoint();
    /// <summary>Prepares and restores fresh pinned guests, then replaces the mounted set without changing authority input.</summary>
    /// <param name="checkpoint">The image captured by this host implementation.</param>
    void RestoreCheckpoint(byte[] checkpoint);
}
