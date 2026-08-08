using System.Globalization;
using System.Text;

using Microsoft.CodeAnalysis;

namespace Puck.Cli.Scan;

// Shared output helpers for the scan analyzers: the 1-based line range every record carries, the
// hand-written JSONL string escaper, and the per-file chunked work-list a fan-out audit consumes. The
// comment, comment-smell and lock analyzers emit the same byFile shape, so the grouping is identical —
// only what populates each line differs.
internal static class ScanJsonl {
    // A location's 1-based start and end line — the pair every analyzer record reports.
    public static (int Start, int End) LineRange(Location location) {
        var span = location.GetLineSpan();

        return ((span.StartLinePosition.Line + 1), (span.EndLinePosition.Line + 1));
    }

    // The span of a construct reported from two anchors (a `lock` keyword through its close paren), so
    // the record covers the header rather than the whole body.
    public static (int Start, int End) LineRange(Location start, Location end) =>
        ((start.GetLineSpan().StartLinePosition.Line + 1), (end.GetLineSpan().EndLinePosition.Line + 1));
    public static string BuildGroupedChunks(Dictionary<string, List<(int Line, string Text)>> byFile, int maxPerChunk) {
        var builder = new StringBuilder(value: "[");
        var firstChunk = true;

        // Densest files first; the ordinal tie-break keeps equal-count files in a fixed order rather
        // than dictionary insertion order, so the work-list is byte-identical run to run.
        foreach (var (file, sites) in byFile.OrderByDescending(keySelector: static pair => pair.Value.Count)
            .ThenBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            var chunkCount = (((sites.Count + maxPerChunk) - 1) / maxPerChunk);

            for (var offset = 0; (offset < sites.Count); offset += maxPerChunk) {
                if (!firstChunk) {
                    builder.Append(value: ',');
                }

                firstChunk = false;
                builder.Append(value: '{')
                    .Append(value: "\"file\":").Append(value: JsonString(value: file)).Append(value: ',')
                    .Append(value: "\"chunk\":").Append(value: (offset / maxPerChunk)).Append(value: ',')
                    .Append(value: "\"chunks\":").Append(value: chunkCount).Append(value: ',')
                    .Append(value: "\"lines\":[");

                var end = Math.Min(val1: (offset + maxPerChunk), val2: sites.Count);

                for (var lineIndex = offset; (lineIndex < end); lineIndex++) {
                    if (lineIndex > offset) {
                        builder.Append(value: ',');
                    }

                    builder.Append(value: sites[lineIndex].Line);
                }

                builder.Append(value: "]}");
            }
        }

        return builder.Append(value: ']').ToString();
    }

    // The densest files first, formatted for the stderr digest every analyzer prints —
    // `<count>  <file>`, top 30 by default.
    public static IEnumerable<string> TopFiles(Dictionary<string, int> perFile, int take = 30) =>
        perFile.OrderByDescending(keySelector: static pair => pair.Value)
            .ThenBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)
            .Take(count: take)
            .Select(selector: static pair => $"{pair.Value,5}  {pair.Key}");

    // Minimal JSON string escaper. The scan output is only ever read back through JsonDocument, so this
    // needs to round-trip, not to be a general serializer.
    public static string JsonString(string value) {
        var builder = new StringBuilder(value: "\"");

        foreach (var character in value) {
            switch (character) {
                case '"':
                    builder.Append(value: "\\\"");
                    break;
                case '\\':
                    builder.Append(value: "\\\\");
                    break;
                case '\n':
                    builder.Append(value: "\\n");
                    break;
                case '\r':
                    builder.Append(value: "\\r");
                    break;
                case '\t':
                    builder.Append(value: "\\t");
                    break;
                default:
                    if (character < 0x20) {
                        builder.Append(value: "\\u").Append(value: ((int)character).ToString(format: "x4", provider: CultureInfo.InvariantCulture));
                    } else {
                        builder.Append(value: character);
                    }

                    break;
            }
        }

        return builder.Append(value: '"').ToString();
    }
}
