using Puck.Cli.Source;

namespace Puck.Cli.Scan;

// The knobs a scan analyzer may read; only the ones an analyzer uses matter to it.
internal sealed class ScanOptions {
    public const int DefaultMaxPerChunk = 40;
    public const int DefaultMinStatements = 4;
    public const int DefaultMinTokens = 30;

    public bool Grouped { get; init; }

    public bool IncludeBlocks { get; init; } = true;
    public int MaxPerChunk { get; init; } = DefaultMaxPerChunk;
    public int MinStatements { get; init; } = DefaultMinStatements;
    public int MinTokens { get; init; } = DefaultMinTokens;
    public string OutDirectory { get; init; } = "";
    // The repository root the comment-smell analyzer resolves cited document paths against. Empty
    // disables the doc-referent probe rather than reporting every citation dangling.
    public string RepositoryRoot { get; init; } = "";
    // The tree the comment-smell analyzer resolves shader-file and define references against. Explicit
    // rather than derived inside the analyzer, so the corpus is not silently widened by a hidden global.
    public string ShaderRoot { get; init; } = "";

    public bool SingleStdout { get; init; }

    // Builds the options from the parsed command line. A non-positive count falls back to its default rather than
    // disabling the gate it feeds. `repositoryRoot` is null when neither the artifacts default nor comment-smells
    // needed one, which is also the only case where `outDirectory` is guaranteed non-null.
    public static ScanOptions Create(
        bool grouped,
        int maxPerChunk,
        int minStatements,
        int minTokens,
        bool noBlocks,
        string? outDirectory,
        string? repositoryRoot,
        bool singleStdout
    ) =>
        new() {
            Grouped = grouped,
            IncludeBlocks = !noBlocks,
            MaxPerChunk = ((maxPerChunk > 0) ? maxPerChunk : DefaultMaxPerChunk),
            MinStatements = ((minStatements > 0) ? minStatements : DefaultMinStatements),
            MinTokens = ((minTokens > 0) ? minTokens : DefaultMinTokens),
            OutDirectory = ((outDirectory is { } outDir)
                ? Path.GetFullPath(path: outDir)
                : Path.Combine(path1: repositoryRoot!, path2: "artifacts", path3: "scan")),
            RepositoryRoot = (repositoryRoot ?? string.Empty),
            ShaderRoot = ((repositoryRoot is null) ? string.Empty : Path.Combine(path1: repositoryRoot, path2: "src")),
            SingleStdout = singleStdout,
        };
}
internal interface ISourceAnalyzer {
    // Builds the analyzer's JSONL records and its grouped work-list over a shared corpus, and writes its
    // own one-line stderr digest. No file IO (the sink owns that), so the same corpus feeds every
    // analyzer in one pass.
    (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options);
}
