// A ripgrep-shaped content search built on a non-backtracking symbolic-derivatives regex engine (linear-time,
// leftmost-longest, intersection/complement/lookaround; no backreferences). Engine credit: repository
// ACKNOWLEDGMENTS.md. Reached as the `puck search` verb; this type is the whole CLI entry.
//
// Semantics that differ from PCRE-style grep and drive how patterns are written:
//   * Matching is LEFTMOST-LONGEST, not greedy/lazy backtracking: for `a|ab` the longer
//     alternative wins regardless of order.
//   * Line mode matches are UNANCHORED (substring), exactly like grep: `cat` hits any line
//     containing "cat". Consequence for complement: a bare `~(.*B.*)` is satisfied by almost
//     any short substring, so "line has A but not B" must anchor the whole line:
//     `^(.*A.*&~(.*B.*))$`. "line has A and B" works unanchored: `.*A.*&.*B.*`.
//   * `_` is any character INCLUDING newline (a literal underscore is `\_`); `.` excludes
//     newline. Span mode (-s) runs the pattern over whole-file text so `_` crosses lines.

using Resharp;

namespace Puck.Cli.Search;

// The `puck search` verb: parse options, validate the pattern once up front, enumerate the files, scan them in parallel
// (one engine instance per worker), and emit. Returns the process exit code (0 matched, 1 no match, 2 usage/pattern).
internal static class SearchCommand {
    public static int Run(string[] args) {
        SearchOptions opt;

        try {
            opt = SearchOptions.Parse(args: args);
        } catch (SearchUsageException ex) {
            Console.Error.WriteLine(value: $"search: {ex.Message}");

            return 2;
        }

        if (opt.ShowHelp) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        // Validate the pattern by compiling once up front. A compile failure is a usage error
        // (exit 2) with the engine's own message verbatim. --files needs no pattern, so skip.
        var finalPattern = (opt.FilesOnly ? null : (opt.Literal ? Regex.Escape(input: opt.Pattern!) : opt.Pattern!));

        if (finalPattern is not null) {
            try {
                _ = SearchScanner.MakeRegex(pattern: finalPattern, ignoreCase: opt.IgnoreCase);
            } catch (Exception ex) {
                Console.Error.WriteLine(value: $"search: bad pattern: {ex.Message}");

                return 2;
            }
        }

        var files = SearchScanner.EnumerateFiles(opt: opt);

        if (files is null) {
            return 2;
        }

        if (opt.FilesOnly) {
            // -q is "exit code only" in every mode, this one included.
            if (!opt.Quiet) {
                foreach (var f in files) {
                    Console.Out.WriteLine(value: CliPaths.ToDisplay(fullPath: f));
                }
            }

            return ((files.Count > 0) ? 0 : 1);
        }

        // The engine builds its DFA lazily and is NOT safe under concurrent access to one Regex, so each worker gets its
        // own instance (localInit). Results are keyed by the original sorted index for deterministic output, and each
        // file scan is guarded so an engine fault degrades to a per-file stderr warning rather than aborting the search.
        var results = new SearchFileResult?[files.Count];

        Parallel.For(
            fromInclusive: 0,
            toExclusive: files.Count,
            localInit: () => SearchScanner.MakeRegex(pattern: finalPattern!, ignoreCase: opt.IgnoreCase),
            body: (i, state, regex) => {
                try {
                    var result = (opt.Span ? SearchScanner.ScanSpan(path: files[i], regex: regex, opt: opt) : SearchScanner.ScanLines(path: files[i], regex: regex, opt: opt));

                    results[i] = result;

                    // -q emits nothing and reports only whether anything matched, so the unscanned files carry no
                    // information once one has hit.
                    if ((result is not null) && opt.Quiet) {
                        state.Stop();
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine(value: $"search: engine fault on {CliPaths.ToDisplay(fullPath: files[i])}: {ex.Message}");
                }

                return regex;
            },
            localFinally: _ => { });

        return SearchEmitter.Emit(results: results, opt: opt);
    }

    private const string HelpText =
        """
        search <pattern> [path ...]   content search (linear-time symbolic-derivatives engine)

          -i, --ignore-case          case-insensitive
          -F, --fixed-strings        literal string (escape the pattern)
          -l, --files-with-matches   files-with-matches only (wins over -c)
          -c, --count                per-file matching-line counts
          -n, --line-number          line numbers on (default)
          -N, --no-line-number       line numbers off
          -A, --after-context <n>    n context lines after
          -B, --before-context <n>   n context lines before
          -C, --context <n>          n context lines before and after
          -g, --glob <glob>          include glob (repeatable; no '/' matches basename)
              --not <glob>           exclude glob (repeatable; no '/' matches a file OR directory basename)
          -s, --span                 span mode: run over whole-file text, print start-end line ranges
          -M, --max-results <n>      max results (default 250, 0 = unlimited)
              --files                enumerate the files that would be searched
          -q, --quiet                quiet: exit code only (--files included)
              --                     end of options: every later argument is pattern/paths
          -h, --help                 this text

        Globs are NOT ripgrep's: brace sets and character classes are literal, and '**'
        requires an intermediate directory, so 'src/**/*.cs' misses 'src/foo.cs'. Repeat
        -g instead of writing a brace set. Exit codes: 0 match, 1 no match, 2 ERROR — do
        not read an empty result as a true negative without checking it.

        The walk prunes .git, artifacts, bin, obj, node_modules, publish,
        BenchmarkDotNet.Artifacts, agent worktrees under .claude/worktrees, and binary
        files; naming one of those paths searches it anyway.
        Engine syntax extensions: _ = any char incl. newline; & = intersection;
        ~(...) = complement. Matching is leftmost-longest; no backreferences.
        Exit codes: 0 matched, 1 no match, 2 usage/pattern error.
        """;
}
