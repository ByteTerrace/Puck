using Puck.Commands;
using Puck.Launcher;

namespace Puck.World.Silo;

/// <summary>
/// One place a submitted command's result becomes a tagged output line, shared by the administrative
/// <c>silo.*</c> session (tag <c>silo</c>) and every per-row session <see cref="SiloConsoleRouting.Register"/>
/// creates (tag = that row's world id) — the verb-output half of the P4 console contract. Engine narration reaches
/// <see cref="Console.Out"/>/<see cref="Console.Error"/> directly and is tagged instead by
/// <see cref="SiloNarrationWriter"/>, which this writer sits beside but never inside — a verb's own tag always wins
/// over whatever <see cref="Server.WorldNarrationScope"/> happens to be ambient when its result is produced.
/// </summary>
public sealed class SiloConsoleTagging(BufferedConsoleOutput output) {
    /// <summary>Writes one tagged result line — REFUSED results to standard error, accepted ones to standard
    /// output — matching <c>TextCommandSource</c>'s own administrative wiring, plus the leading <c>[tag] </c> a
    /// silo run's every line carries.</summary>
    /// <param name="tag">The session's own tag — a world id, or <c>silo</c> for the administrative session.</param>
    /// <param name="result">The submitted line's result.</param>
    public void WriteTagged(string tag, CommandResult result) {
        if (string.IsNullOrEmpty(value: result.Output)) {
            return;
        }

        var line = $"[{tag}] {result.Output}";

        if (result.IsError) {
            output.WriteErrorLine(value: line);
        } else {
            output.WriteLine(value: line);
        }
    }
}
