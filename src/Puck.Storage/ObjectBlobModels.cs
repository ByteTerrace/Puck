namespace Puck.Storage;

/// <summary>The stable identity of one blob: an object (container) id and a relative key within it.</summary>
/// <param name="ObjectId">The container/object the blob lives under (a per-user container id in the cloud model).</param>
/// <param name="Key">The relative key (a forward-slash path) the blob is addressed by within the object.</param>
public readonly record struct ObjectBlobAddress(Guid ObjectId, string Key);

/// <summary>
/// A read blob's bytes paired with its opaque version token — the clobber-guard input a conditional write matches on
/// (§2.5.2). The token orders NOTHING (that is the document's job); it only answers "is this the copy I last saw." Azure
/// yields the download ETag; the local backend yields a content hash (best-effort within one process — the file
/// backend's read/write TOCTOU gap is inherent, not closed here).
/// </summary>
/// <param name="Content">The blob's bytes.</param>
/// <param name="VersionToken">The opaque version token, or <see langword="null"/> when the backend supplies none.</param>
public readonly record struct ObjectBlobContent(ReadOnlyMemory<byte> Content, string? VersionToken);

/// <summary>
/// The outcome of a blob write: whether it landed, whether it was refused by an if-match precondition (the clobber
/// guard fired — someone else wrote since the caller read), and the NEW version token the write produced (Azure's upload
/// ETag or the local backend's recomputed content hash) so the caller need not re-read to learn it.
/// </summary>
/// <remarks>The two failure axes are distinct: a <see cref="ObjectBlobWriteMode.CreateOnly"/> loss (the blob already
/// existed) reports <c>Succeeded false, PreconditionFailed false</c>; a stale if-match reports
/// <c>Succeeded false, PreconditionFailed true</c>. A success reports <c>Succeeded true</c> with the new token.</remarks>
/// <param name="Succeeded">Whether the write landed.</param>
/// <param name="PreconditionFailed">Whether an if-match precondition refused the write (the clobber guard fired).</param>
/// <param name="VersionToken">The new version token on success, the current token on a precondition failure (when the
/// backend can supply it), or <see langword="null"/>.</param>
public readonly record struct ObjectBlobWriteResult(bool Succeeded, bool PreconditionFailed, string? VersionToken);
