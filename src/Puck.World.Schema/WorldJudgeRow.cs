namespace Puck.World;

/// <summary>
/// One judge-window-set asset reference row — a <c>puck.judge.v1</c> document's stable name, its file path
/// (relative to <see cref="AppContext.BaseDirectory"/>, the same convention <see cref="WorldMusicRow"/> uses), and
/// the SHA-256 hex64 pin of the referenced document's own canonical bytes. Referenced by an action lane or
/// interaction opting into rhythm judgment; never a section every world carries.
/// </summary>
/// <param name="Name">The row's stable name — its mutation address.</param>
/// <param name="Source">The referenced document's file path.</param>
/// <param name="Hash">The SHA-256 hex64 of the referenced document's canonical bytes.</param>
public sealed record WorldJudgeRow(string Name, string Source, string Hash);
