namespace Puck.Cli.Search;

// How much of a file's match set the selected output mode actually prints. The scanner produces exactly this much and
// no more.
internal enum SearchDetail {
    // -q and -l print nothing per match, so the scan stops at the first one.
    Existence,

    // -c prints one number per file.
    Count,

    // Line blocks (and spans under -s) print per-match locations.
    Locations,
}
// The parsed search command line. Create is the sole constructor path; every property is set only while it runs.
internal sealed class SearchOptions {
    // The result budget when -M is absent; 0 means unlimited.
    public const int DefaultMaxResults = 250;

    public int After { get; private set; }
    public int Before { get; private set; }
    public bool Counts { get; private set; }

    public List<CliGlob> Exclude { get; } = [];

    public bool FilesOnly { get; private set; }
    public bool FilesWithMatches { get; private set; }
    public bool IgnoreCase { get; private set; }

    public List<CliGlob> Include { get; } = [];
    public bool LineNumbers { get; private set; } = true;

    public bool Literal { get; private set; }

    public int MaxResults { get; private set; } = DefaultMaxResults;
    public List<string> Paths { get; } = [];

    public string? Pattern { get; private set; }
    public bool Quiet { get; private set; }
    public bool Span { get; private set; }
    // The mode precedence, in one place: -q silences everything, then -l wins over -c, then the line-block modes. It
    // is what SearchEmitter applies and what the scanner reads to skip work whose result is never printed.
    public SearchDetail Detail =>
        ((Quiet || FilesWithMatches) ? SearchDetail.Existence : (Counts ? SearchDetail.Count : SearchDetail.Locations));

    // Builds the options from the parsed command line. `after`/`before`/`context` are null when their flag is absent,
    // so -C supplies whichever side -A/-B did not name. `operands` is the positional run: the pattern followed by the
    // paths, or paths only under --files.
    public static SearchOptions Create(
        int? after,
        int? before,
        int? context,
        bool counts,
        string[] exclude,
        bool filesOnly,
        bool filesWithMatches,
        bool ignoreCase,
        string[] include,
        bool literal,
        int maxResults,
        bool noLineNumbers,
        string[] operands,
        bool quiet,
        bool span
    ) {
        var options = new SearchOptions {
            After = ((after ?? context) ?? 0),
            Before = ((before ?? context) ?? 0),
            Counts = counts,
            FilesOnly = filesOnly,
            FilesWithMatches = filesWithMatches,
            IgnoreCase = ignoreCase,
            LineNumbers = !noLineNumbers,
            Literal = literal,
            MaxResults = maxResults,
            Pattern = ((filesOnly || (operands.Length == 0)) ? null : operands[0]),
            Quiet = quiet,
            Span = span,
        };

        foreach (var glob in include) {
            options.Include.Add(item: new CliGlob(glob: glob));
        }

        foreach (var glob in exclude) {
            options.Exclude.Add(item: new CliGlob(glob: glob));
        }

        // --files consumes no pattern, so every operand is a path; otherwise the paths start after it.
        for (var index = (filesOnly ? 0 : 1); (index < operands.Length); index++) {
            options.Paths.Add(item: operands[index]);
        }

        if (options.Paths.Count == 0) {
            options.Paths.Add(item: ".");
        }

        return options;
    }
}
