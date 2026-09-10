using System.CommandLine;

namespace Puck.Cli.Format;

// The `puck format` verb: the source rewriters applied in one parse/write per file. Phase 0 always runs
// the SDK whitespace formatter per project first (the .editorconfig baseline the custom passes layer
// onto); the syntactic passes then run as one re-parsing pipeline, and the semantic `null-pattern` and
// `named-args` passes run as their own disk phases. The exit code is the worst of the phases: 1 for drift in a dry mode
// or a skipped rewrite, 2 for a missing root or a tool failure in write mode.
internal static class FormatCommand {
    public static Command Create() {
        var filesOption = new Option<string>(name: "--files", aliases: ["-Files"]) { Description = "Format only this JSON array of paths relative to <root>; standalone C# files use disposable, built SDK projects." };
        var onlyOption = new Option<string>(name: "--only", aliases: ["-Only"]) { Description = $"Restrict to a comma-separated list of named passes (default: every pass but the three vertical line-wrappers and member-groups). Passes: {FormatPasses.Names}" };
        var rootArgument = new Argument<string>(name: "root") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = static _ => "src", Description = "The tree to sweep, resolved against the working directory (default: src)." };
        var verifyOption = new Option<bool>(name: "--verify", aliases: ["-Verify"]) { Description = "Write nothing; additionally fail on a rewrite that would introduce syntax errors or a pass that is not a fixed point." };
        var whatIfOption = new Option<bool>(name: "--what-if", aliases: ["-WhatIf"]) { Description = "Write nothing; exit 1 on any drift, listing the files." };
        var command = new Command(description: """
            Source rewriters for conventions .editorconfig cannot express.

            <root> resolves against the working directory, the same rule every verb
            applies. Phase 0 (`dotnet format whitespace`) runs first over the projects
            that own corpus files and needs them restored; in WRITE mode it rewrites any
            whitespace drift in that root — run --what-if first on a root you have not
            swept. The semantic named-args pass needs the projects built: an unbuilt
            project's files are SKIPPED and named, in every mode, rather than named
            from a framework-only closure. A pass with syntax errors in its input, or
            whose output would add them, is always dropped and reported, never written.

            Exit codes: 0 clean, 1 drift in a dry mode or a skipped rewrite, 2 missing
            root or a tool failure in write mode.
            """, name: "format") { rootArgument, filesOption, onlyOption, verifyOption, whatIfOption };

        command.Subcommands.Add(item: FormatCiCommand.Create());
        command.Subcommands.Add(item: FormatSubmitCommand.Create());
        command.Validators.Add(item: result => {
            if (result.GetValue(option: onlyOption) is not { } only) {
                return;
            }

            var selected = Selection(only: only);

            // An empty or all-separator value selects nothing, and a run that rewrites nothing reads
            // exactly like a tree that is already clean. The caller meant something; say what exists.
            if (selected.Count == 0) {
                result.AddError(errorMessage: $"-Only named no format pass (known: {FormatPasses.Names}).");

                return;
            }

            foreach (var name in selected) {
                if (!FormatPasses.IsKnown(name: name)) {
                    result.AddError(errorMessage: $"unknown format pass '{name}' (known: {FormatPasses.Names}).");

                    return;
                }
            }
        });
        command.SetAction(action: parseResult => Run(
            manifest: parseResult.GetValue(option: filesOption),
            only: parseResult.GetValue(option: onlyOption),
            root: parseResult.GetRequiredValue(argument: rootArgument),
            verify: parseResult.GetValue(option: verifyOption),
            whatIf: parseResult.GetValue(option: whatIfOption)
        ));
        return command;
    }

    internal static int RunPhases(string root, HashSet<string> selected, bool whatIf, bool verify, string[]? targets = null) {

        // Phase 0: the SDK whitespace formatter establishes the .editorconfig baseline (spacing,
        // alignment, newlines) the custom passes then layer bespoke conventions onto. Disjoint concerns —
        // the result is a fixed point of both — so running it first is safe.
        var result = WhitespacePhase.Run(rootArgument: root, targets: targets, verifyOnly: (whatIf || verify));
        var syntacticPasses = FormatPasses.All.Where(predicate: pass => (!pass.Semantic && selected.Contains(item: pass.Name))).ToList();

        if (syntacticPasses.Count > 0) {
            result = Math.Max(val1: result, val2: SourceRewrite.Run(label: "format", passes: syntacticPasses, rootArgument: root, targets: targets, verify: verify, whatIf: whatIf));
        }

        if (selected.Contains(item: "null-pattern")) {
            result = Math.Max(val1: result, val2: NullPatternPhase.Run(rootArgument: root, targets: targets, verify: verify, whatIf: whatIf));
        }

        if (selected.Contains(item: "named-args")) {
            result = Math.Max(val1: result, val2: NamedArgsPhase.Run(rootArgument: root, targets: targets, verify: verify, whatIf: whatIf));
        }

        return result;
    }

    // An absent -Only takes the registry's own default set; a supplied one is lowercased so pass names
    // compare ordinally against the registry.
    private static HashSet<string> Selection(string? only) => ((only is null)
        ? FormatPasses.DefaultSelection()
        : only.Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: ',')
            .Select(selector: static name => name.ToLowerInvariant())
            .ToHashSet(comparer: StringComparer.Ordinal));
    private static int Run(string? manifest, string? only, string root, bool verify, bool whatIf) {
        var selected = Selection(only: only);

        if (manifest is not null) {
            try {
                return FormatSelection.Run(manifest: manifest, root: root, selected: selected, verify: verify, whatIf: whatIf);
            } catch (Exception error) when ((error is IOException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)) {
                Console.Error.WriteLine(value: $"format: {error.Message}");

                return 2;
            }
        }

        return RunPhases(root: root, selected: selected, whatIf: whatIf, verify: verify);
    }
}
