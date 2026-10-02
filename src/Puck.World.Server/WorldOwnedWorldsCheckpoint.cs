namespace Puck.World.Server;

/// <summary>The catalog's checkpointed state — the identities as document data plus the mutation counter.
/// Excludes <see cref="WorldOwnedWorlds.FilePath"/> (host state — the state directory a fresh instance's own construction
/// resolves) and <see cref="WorldOwnedWorlds.LastReceipt"/> (a read-back-only diagnostic of the most recent submission, the same
/// exclusion class as a body's <c>PressOutcome</c>/<c>StopOutcome</c> — nothing but a read-back verb consults
/// it, and the next real submission repopulates it).</summary>
public sealed record WorldOwnedWorldsCheckpoint(IReadOnlyList<WorldOwnedDocumentCheckpoint> Documents, long Revision);
/// <summary>An owned document and the directory its relative asset references resolve beside.</summary>
/// <param name="DefinitionJson">The serialized document.</param>
/// <param name="DocumentDirectory">The captured asset context, or null for an in-memory document.</param>
public sealed record WorldOwnedDocumentCheckpoint(byte[] DefinitionJson, string? DocumentDirectory);
