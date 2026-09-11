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

using System.CommandLine;

using Resharp;

namespace Puck.Cli.Search;

// The `puck search` verb: validate the pattern once up front, enumerate the files, scan them in parallel (one engine
// instance per worker), and emit. Returns the process exit code (0 matched, 1 no match, 2 pattern or path error).
internal static class SearchCommand {
    public static Command Create() {
        var afterOption = new Option<int?>(name: "--after-context", aliases: ["-A"]) { Description = "Print n context lines after each match." };
        var beforeOption = new Option<int?>(name: "--before-context", aliases: ["-B"]) { Description = "Print n context lines before each match." };
        var contextOption = new Option<int?>(name: "--context", aliases: ["-C"]) { Description = "Print n context lines before and after each match; an explicit -A or -B overrides that side." };
        var countsOption = new Option<bool>(name: "--count", aliases: ["-c"]) { Description = "Print per-file matching-line counts." };
        var excludeOption = new Option<string[]>(name: "--not") { DefaultValueFactory = _ => [], Description = "Exclude glob (repeatable; no '/' matches a file OR directory basename)." };
        var filesOnlyOption = new Option<bool>(name: "--files") { Description = "Enumerate the files that would be searched; no pattern is read." };
        var filesWithMatchesOption = new Option<bool>(name: "--files-with-matches", aliases: ["-l"]) { Description = "Print matching file names only; wins over -c." };
        var ignoreCaseOption = new Option<bool>(name: "--ignore-case", aliases: ["-i"]) { Description = "Match case-insensitively." };
        var includeOption = new Option<string[]>(name: "--glob", aliases: ["-g"]) { DefaultValueFactory = _ => [], Description = "Include glob (repeatable; no '/' matches the basename)." };
        var lineNumbersOption = new Option<bool>(name: "--line-number", aliases: ["-n"]) { Description = "Print line numbers; the default, so this only restates it." };
        var literalOption = new Option<bool>(name: "--fixed-strings", aliases: ["-F"]) { Description = "Treat the pattern as a literal string (escape it)." };
        var maxResultsOption = new Option<int>(name: "--max-results", aliases: ["-M"]) { DefaultValueFactory = _ => SearchOptions.DefaultMaxResults, Description = "Stop after n results; 0 is unlimited." };
        var noLineNumbersOption = new Option<bool>(name: "--no-line-number", aliases: ["-N"]) { Description = "Suppress line numbers; wins over -n." };
        var operandsArgument = new Argument<string[]>(name: "pattern") { Arity = ArgumentArity.ZeroOrMore, DefaultValueFactory = _ => [], Description = "The pattern, then the paths to search (default '.'). Under --files there is no pattern and every operand is a path." };
        var quietOption = new Option<bool>(name: "--quiet", aliases: ["-q"]) { Description = "Print nothing; report the verdict in the exit code alone (--files included)." };
        var spanOption = new Option<bool>(name: "--span", aliases: ["-s"]) { Description = "Span mode: run the pattern over whole-file text and print start-end line ranges." };
        var command = new Command(description: """
            Content search over a linear-time symbolic-derivatives regex engine.

              0  matched
              1  no match
              2  bad pattern, or a path that names nothing on disk

            An empty result is not a true negative until the exit code says so.

            Globs are NOT ripgrep's: brace sets and character classes are literal, and '**' requires an
            intermediate directory, so 'src/**/*.cs' misses 'src/foo.cs'. Repeat -g instead of writing a
            brace set.

            The walk prunes .git, artifacts, bin, obj, node_modules, publish, BenchmarkDotNet.Artifacts,
            agent worktrees under .claude/worktrees, and binary files; naming one of those paths searches
            it anyway.

            Engine syntax extensions: _ = any char incl. newline; & = intersection; ~(...) = complement.
            Matching is leftmost-longest; no backreferences.
            """, name: "search") {
            afterOption,
            beforeOption,
            contextOption,
            countsOption,
            excludeOption,
            filesOnlyOption,
            filesWithMatchesOption,
            ignoreCaseOption,
            includeOption,
            lineNumbersOption,
            literalOption,
            maxResultsOption,
            noLineNumbersOption,
            quietOption,
            spanOption,
            operandsArgument,
        };

        RejectNegative(option: afterOption);
        RejectNegative(option: beforeOption);
        RejectNegative(option: contextOption);
        RejectNegative(option: maxResultsOption);
        command.Validators.Add(item: result => {
            if (!result.GetValue(option: filesOnlyOption) && (result.GetValue(argument: operandsArgument)!.Length == 0)) {
                result.AddError(errorMessage: "A pattern is required unless --files is given.");
            }
        });
        command.SetAction(action: parseResult => Run(opt: SearchOptions.Create(
            after: parseResult.GetValue(option: afterOption),
            before: parseResult.GetValue(option: beforeOption),
            context: parseResult.GetValue(option: contextOption),
            counts: parseResult.GetValue(option: countsOption),
            exclude: parseResult.GetRequiredValue(option: excludeOption),
            filesOnly: parseResult.GetValue(option: filesOnlyOption),
            filesWithMatches: parseResult.GetValue(option: filesWithMatchesOption),
            ignoreCase: parseResult.GetValue(option: ignoreCaseOption),
            include: parseResult.GetRequiredValue(option: includeOption),
            literal: parseResult.GetValue(option: literalOption),
            maxResults: parseResult.GetRequiredValue(option: maxResultsOption),
            noLineNumbers: parseResult.GetValue(option: noLineNumbersOption),
            operands: parseResult.GetRequiredValue(argument: operandsArgument),
            quiet: parseResult.GetValue(option: quietOption),
            span: parseResult.GetValue(option: spanOption))));

        return command;
    }

    // -A/-B/-C/-M are counts, so a negative one is a typo rather than a mode; -M 0 alone means unlimited.
    private static void RejectNegative<T>(Option<T> option) {
        option.Validators.Add(item: result => {
            if ((result.GetValueOrDefault<T>() is int value) && (value < 0)) {
                result.AddError(errorMessage: $"{option.Name} expects a non-negative integer.");
            }
        });
    }
    private static int Run(SearchOptions opt) {
        // Validate the pattern by compiling once up front. A compile failure is exit 2 with the engine's
        // own message verbatim. --files needs no pattern, so skip.
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

        return SearchEmitter.Emit(opt: opt, results: results);
    }
}
