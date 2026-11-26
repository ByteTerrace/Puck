using System.Text.Json;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// What <see cref="WorldPinLedger"/> holds for a <c>state</c> row an operator line wrote, whichever verb wrote it (a
/// cell set, a field of the row, the whole row): one subject for the row, read at the moment the pins are read as the
/// whole row a save would write (<see cref="WorldServer.SettledStateRow"/>), so a restart restores the value the row had
/// reached together with its trait (an advance rate, a dynamics row, a cycle) and never one without the other.
/// </summary>
internal static class WorldStateRowPin {
    /// <summary>Returns the subject every line that writes a <c>state</c> row shares.</summary>
    /// <param name="row">The row's name.</param>
    /// <returns>The subject.</returns>
    public static string Subject(string row) => $"state {row}";
    /// <summary>Returns the reader of the line that sets a <c>state</c> row again as it stands when read.</summary>
    /// <param name="server">The authority that owns the row.</param>
    /// <param name="row">The row's name.</param>
    /// <returns>A reader answering the whole-row <c>world.row.set</c> line, or <see langword="null"/> once the document
    /// no longer authors the row.</returns>
    public static Func<string?> Line(WorldServer server, string row) => () => ((server.SettledStateRow(name: row) is { } settled)
        ? $"world.row.set state {JsonSerializer.SerializeToNode(value: settled, jsonTypeInfo: WorldJsonContext.Authored.WorldStateRow)!.ToJsonString()}"
        : null
    );
}
