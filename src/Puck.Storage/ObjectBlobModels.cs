namespace Puck.Storage;

/// <summary>The stable identity of one blob: an object (container) id and a relative key within it.</summary>
/// <param name="ObjectId">The container/object the blob lives under (a per-user container id in the cloud model).</param>
/// <param name="Key">The relative key (a forward-slash path) the blob is addressed by within the object.</param>
public readonly record struct ObjectBlobAddress(Guid ObjectId, string Key);
/// <summary>
/// A read blob's bytes paired with its opaque version token — the clobber-guard input a conditional write matches on
/// (§2.5.2). The token orders NOTHING (that is the document's job); it only answers "is this the copy I last saw."
/// Each backend chooses its token: a cloud store's entity tag, or the directory backend's content hash.
/// </summary>
/// <param name="Content">The blob's bytes.</param>
/// <param name="VersionToken">The opaque version token, or <see langword="null"/> when the backend supplies none.</param>
public readonly record struct ObjectBlobContent(ReadOnlyMemory<byte> Content, string? VersionToken) {
    /// <summary>Returns whether a whole-blob read found a blob holding exactly <paramref name="expected"/>: the
    /// buffered comparison behind <see cref="IObjectBlobStore.ContentEqualsAsync"/> for a store that cannot stream.</summary>
    /// <param name="read">The pending read of the blob.</param>
    /// <param name="expected">The bytes the blob must hold.</param>
    /// <returns><see langword="true"/> when the read found the blob and its bytes equal <paramref name="expected"/>.</returns>
    public static async ValueTask<bool> MatchesAsync(ValueTask<ObjectBlobContent?> read, ReadOnlyMemory<byte> expected) =>
        ((await read.ConfigureAwait(continueOnCapturedContext: false)) is { } found) && found.Content.Span.SequenceEqual(other: expected.Span);
}
/// <summary>
/// The outcome of a blob write: whether it landed, whether it was refused by an if-match precondition (the clobber
/// guard fired — someone else wrote since the caller read), and the NEW version token the write produced (the store's
/// token for the written bytes) so the caller need not re-read to learn it.
/// </summary>
/// <remarks>The two failure axes are distinct: a <see cref="ObjectBlobWriteMode.CreateOnly"/> loss (the blob already
/// existed) reports <c>Succeeded false, PreconditionFailed false</c>; a stale if-match reports
/// <c>Succeeded false, PreconditionFailed true</c>. A success reports <c>Succeeded true</c> with the new token.</remarks>
/// <param name="Succeeded">Whether the write landed.</param>
/// <param name="PreconditionFailed">Whether an if-match precondition refused the write (the clobber guard fired).</param>
/// <param name="VersionToken">The new version token on success, the current token on a precondition failure (when the
/// backend can supply it), or <see langword="null"/>.</param>
public readonly record struct ObjectBlobWriteResult(bool Succeeded, bool PreconditionFailed, string? VersionToken);
