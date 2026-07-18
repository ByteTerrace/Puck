using System.Globalization;
using Puck.Commands;

namespace Puck.Demo;

/// <summary>
/// Renders a <see cref="TickTranscriptEntry"/> as one readable console line — shared by <c>tick.explain</c>'s
/// backward-looking narration and <see cref="TickTranscriptRecorder"/>'s live <c>tick.watch</c> echo, so both read
/// identically. Engine vocabulary, not hex dumps: a quiet tick says so; hashes are shown short (the first 8 hex
/// digits) unless full output is requested.
/// </summary>
internal static class TickNarration {
    /// <summary>Formats one tick's narration line.</summary>
    /// <param name="entry">The recorded tick.</param>
    /// <param name="tag">The bracketed verb tag to prefix (e.g. <c>"tick.explain"</c> or <c>"tick.watch"</c>).</param>
    /// <param name="full">Whether to show the full 64-bit hash instead of the short 8-digit form.</param>
    public static string Describe(TickTranscriptEntry entry, string tag, bool full) {
        var hashPart = (((entry.HashBefore is { } before) && (entry.HashAfter is { } after))
            ? $" hash {FormatHash(hash: before, full: full)} -> {FormatHash(hash: after, full: full)}{((before == after) ? " (unchanged)" : "")}"
            : "");

        if (entry.CommandCount == 0) {
            return $"[{tag}: tick {entry.Tick} — quiet tick{hashPart}]";
        }

        var commands = string.Join(separator: ", ", values: Enumerable.Range(start: 0, count: entry.CommandCount).Select(selector: entry.CommandAt));
        var overflow = ((entry.OverflowCount > 0) ? $" (+{entry.OverflowCount} more, dropped)" : "");

        return $"[{tag}: tick {entry.Tick} — {commands}{overflow}{hashPart}]";
    }

    private static string FormatHash(ulong hash, bool full) {
        var text = hash.ToString(format: "X16", provider: CultureInfo.InvariantCulture);

        return $"0x{(full ? text : text[..8])}";
    }
}
