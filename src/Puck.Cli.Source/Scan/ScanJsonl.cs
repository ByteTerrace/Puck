using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.CodeAnalysis;

namespace Puck.Cli.Scan;

// The sites a line-site analyzer finds, each one JSONL record of file, line range, kind and text, collected with
// the per-file counts its digest prints and the per-file work-list --grouped writes. Every analyzer that reports
// sites rather than clusters emits through it, so the record shape and the grouping are one implementation.
internal sealed class ScanSites {
    private readonly Dictionary<string, List<(int Line, string Text)>> m_byFile = new(comparer: StringComparer.Ordinal);
    private readonly StringBuilder m_jsonl = new();
    private readonly SortedDictionary<string, int> m_kinds = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, int> m_perFile = new(comparer: StringComparer.Ordinal);

    // The number of sites recorded.
    public int Count { get; private set; }
    // The number of files holding at least one site.
    public int FileCount => m_perFile.Count;

    // Records one site.
    public void Add(string file, int line, int endLine, string kind, string text, string? extra = null) {
        m_jsonl.Append(value: '{')
            .Append(value: "\"file\":").Append(value: ScanJsonl.JsonString(value: file)).Append(value: ',')
            .Append(value: "\"line\":").Append(value: line).Append(value: ',')
            .Append(value: "\"endLine\":").Append(value: endLine).Append(value: ',')
            .Append(value: "\"kind\":").Append(value: ScanJsonl.JsonString(value: kind)).Append(value: ',')
            .Append(value: "\"text\":").Append(value: ScanJsonl.JsonString(value: text));
        if (extra is { Length: > 0 }) { m_jsonl.Append(value: ',').Append(value: extra); }
        m_jsonl.Append(value: "}\n");
        Count++;
        m_kinds[kind] = (m_kinds.GetValueOrDefault(key: kind) + 1);
        m_perFile[file] = (m_perFile.GetValueOrDefault(key: file) + 1);
        if (!m_byFile.TryGetValue(key: file, value: out var sites)) {
            sites = [];
            m_byFile[file] = sites;
        }

        sites.Add(item: (line, text));
    }
    // The site counts by kind, `<n> <kind>` in ordinal order, or "none".
    public string Breakdown() =>
        ((Count == 0)
            ? "none"
            : string.Join(
                separator: ", ",
                values: m_kinds.Select(selector: static pair => $"{pair.Value} {pair.Key}")
            ));
    // Writes the digest line and the densest files to stderr, and returns the records and the grouped work-list.
    public (string Jsonl, string Grouped) Finish(int maxPerChunk, string? digest = null) {
        if (digest is not null) {
            Console.Error.WriteLine(value: digest);
            foreach (var line in ScanJsonl.TopFiles(perFile: m_perFile)) { Console.Error.WriteLine(value: line); }
        }

        return (m_jsonl.ToString(), ScanJsonl.BuildGroupedChunks(
            byFile: m_byFile,
            maxPerChunk: maxPerChunk
        ));
    }
}
// Shared output helpers for the scan analyzers: the 1-based line range every record carries, the
// hand-written JSONL string escaper, and the per-file chunked work-list a fan-out audit consumes. The
// comment, comment-smell and line-site analyzers emit the same byFile shape, so the grouping is identical —
// only what populates each line differs.
internal static class ScanJsonl {
    public static string BuildGroupedChunks(Dictionary<string, List<(int Line, string Text)>> byFile, int maxPerChunk) {
        var builder = new StringBuilder(value: "[");
        var firstChunk = true;

        // Densest files first; the ordinal tie-break keeps equal-count files in a fixed order rather
        // than dictionary insertion order, so the work-list is byte-identical run to run.
        foreach (var (file, sites) in byFile.OrderByDescending(keySelector: static pair => pair.Value.Count)
            .ThenBy(
            keySelector: static pair => pair.Key,
            comparer: StringComparer.Ordinal
        )) {
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

                var end = Math.Min(
                    val1: (offset + maxPerChunk),
                    val2: sites.Count
                );

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
    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The scan output is UTF-8 text read back through JsonDocument, so markup characters and non-ASCII text stay literal.
    public static string JsonString(string value) => JsonSerializer.Serialize(options: StringOptions, value: value);
    // A location's 1-based start and end line — the pair every analyzer record reports.
    public static (int Start, int End) LineRange(Location location) {
        var span = location.GetLineSpan();

        return ((span.StartLinePosition.Line + 1), (span.EndLinePosition.Line + 1));
    }
    // The span of a construct reported from two anchors (a `lock` keyword through its close paren), so
    // the record covers the header rather than the whole body.
    public static (int Start, int End) LineRange(Location start, Location end) =>
        ((start.GetLineSpan().StartLinePosition.Line + 1), (end.GetLineSpan().EndLinePosition.Line + 1));
    // The densest files first, formatted for the stderr digest every analyzer prints —
    // `<count>  <file>`, top 30 by default.
    public static IEnumerable<string> TopFiles(Dictionary<string, int> perFile, int take = 30) =>
        perFile.OrderByDescending(keySelector: static pair => pair.Value)
            .ThenBy(
            keySelector: static pair => pair.Key,
            comparer: StringComparer.Ordinal
        )
            .Take(count: take)
            .Select(selector: static pair => $"{pair.Value,5}  {pair.Key}");
}
