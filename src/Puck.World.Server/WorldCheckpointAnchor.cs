namespace Puck.World.Server;

/// <summary>The root a checkpoint names a file or directory relative to. A checkpoint is durable and crosses machines,
/// so it never carries a machine-local absolute path: the restoring host supplies the root.</summary>
public enum WorldCheckpointAnchor : byte {
    /// <summary>Names nothing on this machine: an owned document held only in memory, or a journal base whose origin is
    /// a hosted world's store identity rather than a file. Nothing resolves against a root.</summary>
    None,
    /// <summary>Relative to the owned-world catalog's own directory, where loaded owned documents live.</summary>
    Catalog,
    /// <summary>Relative to the hosting world's document directory, where a seeded owned document resolves its assets
    /// and a loaded journal base was read from.</summary>
    World,
}
