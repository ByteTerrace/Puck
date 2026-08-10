using Puck.Cli.Source;

namespace Puck.Cli.Scan;

// The knobs a scan analyzer may read; only the ones an analyzer uses matter to it.
internal sealed class ScanOptions {
    public bool Grouped { get; init; }
    public bool IncludeBlocks { get; init; } = true;
    public int MaxPerChunk { get; init; } = 40;
    public int MinStatements { get; init; } = 4;
    public int MinTokens { get; init; } = 30;
    public string OutDirectory { get; init; } = "";

    // The repository root the comment-smell analyzer resolves cited document paths against. Empty
    // disables the doc-referent probe rather than reporting every citation dangling.
    public string RepositoryRoot { get; init; } = "";

    // The tree the comment-smell analyzer resolves shader-file and define references against. Explicit
    // rather than derived inside the analyzer, so the corpus is not silently widened by a hidden global.
    public string ShaderRoot { get; init; } = "";
    public bool SingleStdout { get; init; }
}
internal interface ISourceAnalyzer {
    // Builds the analyzer's JSONL records and its grouped work-list over a shared corpus, and writes its
    // own one-line stderr digest. No file IO (the sink owns that), so the same corpus feeds every
    // analyzer in one pass.
    (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options);
}
