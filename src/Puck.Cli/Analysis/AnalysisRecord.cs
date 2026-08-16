using System.Globalization;
using System.Text;

using Puck.Cli.Scan;

namespace Puck.Cli.Analysis;

// One emitted line of either analysis verb. Path is working-directory-relative and forward-slashed;
// Line and Column are 1-based. Relation is what the position IS — `decl` for a declaration, `ref` for a
// reference, `impl`/`override`/`derived` for the relationship queries, `cref` for a documentation
// reference. Kind is the symbol or declaration kind, empty where the relation already says it. Name is
// the fully qualified symbol; Detail carries a base list where there is one.
internal readonly record struct AnalysisRecord(string Path, int Line, int Column, string Relation, string Kind, string Name, string? Detail);
// Renders analysis records and reports the process exit code. Records are printed in the order the verb
// produced them — each verb owns its own ordering, and both orderings are total — so the emitter never
// reorders and never adds a summary line: stdout carries records and nothing else.
internal static class AnalysisEmitter {
    // 0 when anything was found, 1 when nothing was.
    public static int Emit(IReadOnlyList<AnalysisRecord> records, bool json, bool quiet) {
        if (!quiet) {
            foreach (var record in records) {
                Console.Out.WriteLine(value: (json ? ToJson(record: record) : ToText(record: record)));
            }
        }

        return ((records.Count > 0) ? 0 : 1);
    }

    // `path:line:col relation [kind] name[ : detail]` — the path leads so a line parses like a search hit
    // and pastes into an editor as a jump target.
    private static string ToText(AnalysisRecord record) {
        var builder = new StringBuilder()
            .Append(value: record.Path)
            .Append(value: ':')
            .Append(value: record.Line.ToString(provider: CultureInfo.InvariantCulture))
            .Append(value: ':')
            .Append(value: record.Column.ToString(provider: CultureInfo.InvariantCulture))
            .Append(value: ' ')
            .Append(value: record.Relation);

        if (record.Kind.Length > 0) {
            builder.Append(value: ' ').Append(value: record.Kind);
        }

        builder.Append(value: ' ').Append(value: record.Name);

        if (record.Detail is { Length: > 0 } detail) {
            builder.Append(value: " : ").Append(value: detail);
        }

        return builder.ToString();
    }
    // One JSON object per line, keys in a fixed order; `detail` is present only when there is one.
    private static string ToJson(AnalysisRecord record) {
        var builder = new StringBuilder(value: "{")
            .Append(value: "\"path\":").Append(value: ScanJsonl.JsonString(value: record.Path)).Append(value: ',')
            .Append(value: "\"line\":").Append(value: record.Line.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ',')
            .Append(value: "\"column\":").Append(value: record.Column.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ',')
            .Append(value: "\"relation\":").Append(value: ScanJsonl.JsonString(value: record.Relation)).Append(value: ',')
            .Append(value: "\"kind\":").Append(value: ScanJsonl.JsonString(value: record.Kind)).Append(value: ',')
            .Append(value: "\"name\":").Append(value: ScanJsonl.JsonString(value: record.Name));

        if (record.Detail is { Length: > 0 } detail) {
            builder.Append(value: ",\"detail\":").Append(value: ScanJsonl.JsonString(value: detail));
        }

        return builder.Append(value: '}').ToString();
    }
}
