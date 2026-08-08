namespace Puck.Cli.Search;

// Renders scan results to stdout in the mode the options select and returns the process exit code: 0 when anything
// matched, 1 when nothing did. Mode precedence is -q, then -l over -c, then spans or line blocks — the order
// SearchOptions.Detail records for the scanner.
internal static class SearchEmitter {
    public static int Emit(SearchFileResult?[] results, SearchOptions opt) {
        var matched = false;
        var emitted = 0;
        var wroteBlock = false;
        var context = Math.Max(val1: opt.Before, val2: opt.After);

        foreach (var r in results) {
            if (r is null) {
                continue;
            }

            matched = true;

            if (opt.Quiet) {
                return 0;
            }

            if (opt.FilesWithMatches) {
                Console.Out.WriteLine(value: CliPaths.ToDisplay(fullPath: r.Path));

                if (Cap(emitted: ref emitted, opt: opt)) {
                    return 0;
                }

                continue;
            }

            if (opt.Counts) {
                Console.Out.WriteLine(value: $"{CliPaths.ToDisplay(fullPath: r.Path)}:{r.Count}");

                continue;
            }

            if (opt.Span) {
                foreach (var (s, e) in r.Spans!) {
                    Console.Out.WriteLine(value: $"{CliPaths.ToDisplay(fullPath: r.Path)}:{(s + 1)}-{(e + 1)}");

                    if (Cap(emitted: ref emitted, opt: opt)) {
                        return 0;
                    }
                }

                continue;
            }

            if ((context > 0) && wroteBlock) {
                Console.Out.WriteLine(value: "--");
            }

            if (EmitLineBlock(r: r, opt: opt, emitted: ref emitted)) {
                return 0;
            }

            wroteBlock = true;
        }

        return (matched ? 0 : 1);
    }

    private static bool EmitLineBlock(SearchFileResult r, SearchOptions opt, ref int emitted) {
        var lines = r.Lines!;
        var hits = r.Hits!;
        var disp = CliPaths.ToDisplay(fullPath: r.Path);
        var hitSet = new HashSet<int>(collection: hits);
        var lastPrinted = -1;
        var context = Math.Max(val1: opt.Before, val2: opt.After);

        foreach (var h in hits) {
            var from = Math.Max(val1: 0, val2: (h - opt.Before));
            var to = Math.Min(val1: (lines.Length - 1), val2: (h + opt.After));

            if ((context > 0) && (lastPrinted >= 0) && (from > (lastPrinted + 1))) {
                Console.Out.WriteLine(value: "--");
            }

            if (from <= lastPrinted) {
                from = (lastPrinted + 1);
            }

            for (var i = from; (i <= to); i++) {
                var isHit = hitSet.Contains(item: i);
                var sep = (isHit ? ":" : "-");
                var prefix = (opt.LineNumbers ? (((disp + sep) + (i + 1)) + sep) : (disp + sep));

                Console.Out.WriteLine(value: (prefix + lines[i]));
                lastPrinted = i;

                if (isHit && Cap(emitted: ref emitted, opt: opt)) {
                    return true;
                }
            }
        }

        return false;
    }
    private static bool Cap(ref int emitted, SearchOptions opt) {
        emitted++;

        return ((opt.MaxResults != 0) && (emitted >= opt.MaxResults));
    }
}
