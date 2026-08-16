namespace Puck.Cli.Search;

// Raised by SearchOptions.Parse on a bad command line; the caller maps it to exit 2 with the message.
internal sealed class SearchUsageException(string message) : Exception(message);
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
// The parsed search command line. Parse is the sole constructor path; every property is set only during parsing.
internal sealed class SearchOptions {
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
    public int MaxResults { get; private set; } = 250;
    public List<string> Paths { get; } = [];
    public string? Pattern { get; private set; }
    public bool Quiet { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool Span { get; private set; }
    // The mode precedence, in one place: -q silences everything, then -l wins over -c, then the line-block modes. It
    // is what SearchEmitter applies and what the scanner reads to skip work whose result is never printed.
    public SearchDetail Detail =>
        ((Quiet || FilesWithMatches) ? SearchDetail.Existence : (Counts ? SearchDetail.Count : SearchDetail.Locations));

    public static SearchOptions Parse(string[] args) {
        var o = new SearchOptions();
        var positional = new List<string>();
        var afterDoubleDash = false;

        for (var i = 0; (i < args.Length); i++) {
            var a = args[i];

            if (afterDoubleDash || (a.Length == 0) || (a[0] != '-') || (a == "-")) {
                positional.Add(item: a);

                continue;
            }

            switch (a) {
                case "--":
                    afterDoubleDash = true;
                    break;
                case "-h" or "--help":
                    o.ShowHelp = true;
                    break;
                case "-i" or "--ignore-case":
                    o.IgnoreCase = true;
                    break;
                case "-F" or "--fixed-strings":
                    o.Literal = true;
                    break;
                case "-l" or "--files-with-matches":
                    o.FilesWithMatches = true;
                    break;
                case "-c" or "--count":
                    o.Counts = true;
                    break;
                case "-n" or "--line-number":
                    o.LineNumbers = true;
                    break;
                case "-N" or "--no-line-number":
                    o.LineNumbers = false;
                    break;
                case "-s" or "--span":
                    o.Span = true;
                    break;
                case "-q" or "--quiet":
                    o.Quiet = true;
                    break;
                case "--files":
                    o.FilesOnly = true;
                    break;
                case "-A" or "--after-context":
                    o.After = NextInt(args: args, flag: a, i: ref i);
                    break;
                case "-B" or "--before-context":
                    o.Before = NextInt(args: args, flag: a, i: ref i);
                    break;
                case "-C" or "--context":
                    var c = NextInt(args: args, flag: a, i: ref i);
                    o.Before = c;
                    o.After = c;
                    break;
                case "-M" or "--max-results":
                    o.MaxResults = NextInt(args: args, flag: a, i: ref i);
                    break;
                case "-g" or "--glob":
                    o.Include.Add(item: new CliGlob(glob: NextArg(args: args, flag: a, i: ref i)));
                    break;
                case "--not":
                    o.Exclude.Add(item: new CliGlob(glob: NextArg(args: args, flag: a, i: ref i)));
                    break;
                default:
                    throw new SearchUsageException(message: $"unknown option '{a}' (try --help)");
            }
        }

        if (o.ShowHelp) {
            return o;
        }

        if (o.FilesOnly) {
            o.Paths.AddRange(collection: positional);
        } else {
            if (positional.Count == 0) {
                throw new SearchUsageException(message: "missing pattern (try --help)");
            }

            o.Pattern = positional[0];
            o.Paths.AddRange(collection: positional.Skip(count: 1));
        }

        if (o.Paths.Count == 0) {
            o.Paths.Add(item: ".");
        }

        return o;
    }

    private static string NextArg(string[] args, ref int i, string flag) {
        if ((i + 1) >= args.Length) {
            throw new SearchUsageException(message: $"{flag} requires a value");
        }

        return args[++i];
    }
    private static int NextInt(string[] args, ref int i, string flag) {
        var raw = NextArg(args: args, flag: flag, i: ref i);

        if (!int.TryParse(result: out var v, s: raw) || (v < 0)) {
            throw new SearchUsageException(message: $"{flag} expects a non-negative integer, got '{raw}'");
        }

        return v;
    }
}
