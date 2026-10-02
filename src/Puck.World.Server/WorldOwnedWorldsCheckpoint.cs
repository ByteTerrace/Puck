namespace Puck.World.Server;

/// <summary>The catalog's checkpointed state — the identities as document data plus the mutation counter.
/// Excludes <see cref="WorldOwnedWorlds.FilePath"/> (host state — the state directory a fresh instance's own construction
/// resolves) and <see cref="WorldOwnedWorlds.LastReceipt"/> (a read-back-only diagnostic of the most recent submission, the same
/// exclusion class as a body's <c>PressOutcome</c>/<c>StopOutcome</c> — nothing but a read-back verb consults
/// it, and the next real submission repopulates it).</summary>
public sealed record WorldOwnedWorldsCheckpoint(IReadOnlyList<WorldOwnedDocumentCheckpoint> Documents, long Revision);
/// <summary>An owned document and where its relative asset references resolve.</summary>
/// <param name="DefinitionJson">The serialized document.</param>
/// <param name="Anchor">The root the asset directory is named relative to.</param>
/// <param name="RelativeDirectory">The asset directory under <paramref name="Anchor"/>'s root, forward-slashed and empty
/// for the root itself; <see langword="null"/> exactly when <paramref name="Anchor"/> is
/// <see cref="WorldCheckpointAnchor.None"/>.</param>
public sealed record WorldOwnedDocumentCheckpoint(byte[] DefinitionJson, WorldCheckpointAnchor Anchor, string? RelativeDirectory);
