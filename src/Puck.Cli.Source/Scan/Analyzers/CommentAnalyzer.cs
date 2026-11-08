using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// One JSONL record per non-XML comment (the SingleLine // and MultiLine /* */ trivia kinds; the /// and
// /** */ doc-comment kinds are skipped, which is why "//" inside a string never registers). --grouped
// buckets the comments into per-file chunks of line numbers — the work-list a fan-out staleness audit
// fans across, big files split so no reviewer judges too many comments at once. The json is hand-written:
// one record shape, emitted once, so a serializer would buy nothing.
internal sealed class CommentAnalyzer : ISourceAnalyzer {
    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var sites = new ScanSites();
        var single = 0;
        var multi = 0;

        foreach (var parsed in corpus.Files) {
            foreach (var (startLine, endLine, isSingle, text) in InlineComments.CollectComments(parsed: parsed)) {
                sites.Add(endLine: endLine, file: parsed.Relative, kind: (isSingle ? "single" : "multi"), line: startLine, text: text);
                if (isSingle) { single++; } else { multi++; }
            }
        }

        return sites.Finish(
            digest: $"scan[comments]: {sites.Count} inline comments ({single} single-line, {multi} block) across {sites.FileCount} files (of {corpus.FileCount} scanned).",
            maxPerChunk: options.MaxPerChunk
        );
    }
}
