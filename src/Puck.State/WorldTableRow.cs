namespace Puck.State;

/// <summary>
/// One static lookup-table asset reference row — a <c>puck.table.v1</c> document's stable name, its file path
/// (relative to <see cref="AppContext.BaseDirectory"/>, the convention <see cref="P:Puck.World.WorldMusicRow.Source"/> uses), and
/// the SHA-256 hex64 pin of the document's canonical bytes. A rule reads it through
/// <c>$table:&lt;name&gt;:&lt;key&gt;</c>; nothing writes it, and it is not simulation state.
/// </summary>
/// <param name="Name">The row's stable name.</param>
/// <param name="Source">The referenced document's file path.</param>
/// <param name="Hash">The SHA-256 hex64 of the referenced document's canonical bytes.</param>
public sealed record WorldTableRow(string Name, string Source, string Hash);
