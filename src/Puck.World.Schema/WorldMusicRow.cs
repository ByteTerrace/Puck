using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// A named <c>puck.music.v1</c> score and the SHA-256 pin of its canonical bytes.
/// Authoring resolves its file beside the world document. Recipient projections carry the pinned bytes.
/// </summary>
/// <param name="Name">The row's stable name — its mutation address.</param>
/// <param name="Source">The authored document path or the embedded canonical score in a recipient projection.</param>
/// <param name="Hash">The SHA-256 hex64 of the referenced document's canonical bytes.</param>
public sealed record WorldMusicRow(
    [property: DocumentText("a music row name is the music section's own namespace")] string Name,
    [property: DocumentText("a music row's source is a document path")] string Source,
    [property: DocumentText("a content digest")] string Hash
);
