namespace Puck.World.Server;

/// <summary>The catalog's checkpointed state — the identities as document data plus the mutation counter.
/// Excludes <see cref="WorldOwnedWorlds.FilePath"/> (host state — the state directory a fresh instance's own construction
/// resolves) and <see cref="WorldOwnedWorlds.LastReceipt"/> (a read-back-only diagnostic of the most recent submission, the same
/// exclusion class as a body's <c>PressOutcome</c>/<c>StopOutcome</c> — nothing but a read-back verb consults
/// it, and the next real submission repopulates it).</summary>
public sealed record WorldOwnedWorldsCheckpoint(IReadOnlyList<WorldOwnedDocumentCheckpoint> Documents, long Revision);
/// <summary>The root an owned document's asset directory is named relative to in a checkpoint. A checkpoint is durable
/// and crosses machines, so it never carries a machine-local absolute path: the restoring host supplies the root.</summary>
public enum WorldOwnedDocumentAnchor : byte {
    /// <summary>The document is in memory and resolves no relative path.</summary>
    None,
    /// <summary>Relative to the owned-world catalog's own directory, where loaded owned documents live.</summary>
    Catalog,
    /// <summary>Relative to the hosting world's document directory, where a seeded owned document resolves its assets.</summary>
    World,
}
/// <summary>An owned document and where its relative asset references resolve.</summary>
/// <param name="DefinitionJson">The serialized document.</param>
/// <param name="Anchor">The root the asset directory is named relative to.</param>
/// <param name="RelativeDirectory">The asset directory under <paramref name="Anchor"/>'s root, forward-slashed and empty
/// for the root itself; <see langword="null"/> exactly when <paramref name="Anchor"/> is
/// <see cref="WorldOwnedDocumentAnchor.None"/>.</param>
public sealed record WorldOwnedDocumentCheckpoint(byte[] DefinitionJson, WorldOwnedDocumentAnchor Anchor, string? RelativeDirectory);
