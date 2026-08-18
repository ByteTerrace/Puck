namespace Puck.World;

/// <summary>
/// One music-score asset reference row — a <c>puck.music.v1</c> document's stable name, its file path (relative to
/// <see cref="AppContext.BaseDirectory"/>, the same convention <c>WorldAddonRow.ModulePath</c> already establishes
/// for a machine-local asset shipped beside the executable), and the SHA-256 hex64 pin of the referenced document's
/// own canonical bytes. Never embedded: the document is loaded, canonicalized, and hash-verified where it is
/// compiled (<c>Server.WorldServer</c>'s construction), the same load-then-pin discipline
/// <c>Puck.Text.FontAtlasSourceResolver.ResolvePinnedContained</c> already applies to a font asset.
/// </summary>
/// <param name="Name">The row's stable name — its mutation address.</param>
/// <param name="Source">The referenced document's file path.</param>
/// <param name="Hash">The SHA-256 hex64 of the referenced document's canonical bytes.</param>
public sealed record WorldMusicRow(string Name, string Source, string Hash);
