using System.Text;

using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// One JSONL record per non-XML comment (the SingleLine // and MultiLine /* */ trivia kinds; the /// and
// /** */ doc-comment kinds are skipped, which is why "//" inside a string never registers). -Grouped
// buckets the comments into per-file chunks of line numbers — the work-list a fan-out staleness audit
// fans across, big files split so no reviewer judges too many comments at once. The json is hand-written:
// one record shape, emitted once, so a serializer would buy nothing.
internal sealed class CommentAnalyzer : ISourceAnalyzer {
    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var jsonl = new StringBuilder();
        var perFile = new Dictionary<string, int>();
        var byFile = new Dictionary<string, List<(int Line, string Text)>>();
        var single = 0;
        var multi = 0;

        foreach (var parsed in corpus.Files) {
            var relative = parsed.Relative;

            foreach (var trivia in parsed.Root.DescendantTrivia()) {
                var kind = trivia.Kind();
                var isSingle = (kind == SyntaxKind.SingleLineCommentTrivia);

                if (!isSingle && (kind != SyntaxKind.MultiLineCommentTrivia)) {
                    continue;
                }

                var (startLine, endLine) = ScanJsonl.LineRange(location: trivia.GetLocation());
                var text = trivia.ToString().Trim();

                jsonl.Append(value: '{')
                    .Append(value: "\"file\":").Append(value: ScanJsonl.JsonString(value: relative)).Append(value: ',')
                    .Append(value: "\"line\":").Append(value: startLine).Append(value: ',')
                    .Append(value: "\"endLine\":").Append(value: endLine).Append(value: ',')
                    .Append(value: "\"kind\":").Append(value: (isSingle ? "\"single\"" : "\"multi\"")).Append(value: ',')
                    .Append(value: "\"text\":").Append(value: ScanJsonl.JsonString(value: text))
                    .Append(value: "}\n");

                if (isSingle) {
                    single++;
                } else {
                    multi++;
                }

                perFile[relative] = (perFile.GetValueOrDefault(key: relative) + 1);

                if (!byFile.TryGetValue(key: relative, value: out var lines)) {
                    lines = [];
                    byFile[relative] = lines;
                }

                lines.Add(item: (startLine, text));
            }
        }

        var total = (single + multi);

        Console.Error.WriteLine(
            value: $"scan[comments]: {total} inline comments ({single} single-line, {multi} block) across {perFile.Count} files (of {corpus.FileCount} scanned).");

        foreach (var line in ScanJsonl.TopFiles(perFile: perFile)) {
            Console.Error.WriteLine(value: line);
        }

        return (jsonl.ToString(), ScanJsonl.BuildGroupedChunks(byFile: byFile, maxPerChunk: options.MaxPerChunk));
    }
}
