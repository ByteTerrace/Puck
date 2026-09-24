using System.CommandLine;

namespace Puck.Cli.Format;

// The `puck format` verb: every source kind Puck owns, formatted to its one canonical form. A .puck source is parsed
// and printed back by PuckPrinter. A C# source goes through the SDK whitespace formatter first (phase 0, the
// .editorconfig baseline the custom passes layer onto); the syntactic passes then run as one re-parsing pipeline, and
// the semantic `null-pattern` and `named-args` passes run as one disk phase that parses, evaluates and compiles each
// owning project once for both. The exit code is the worst of the phases.
internal static class FormatCommand {
    internal static int RunPhases(CompileClosures closures, string root, string configuration, HashSet<string> selected, string[] targets, bool check) {

        // Phase 0: the SDK whitespace formatter establishes the .editorconfig baseline (spacing,
        // alignment, newlines) the custom passes then layer bespoke conventions onto. Disjoint concerns —
        // the result is a fixed point of both — so running it first is safe.
        var result = WhitespacePhase.Run(
            check: check,
            rootArgument: root,
            targets: targets
        );
        var syntacticPasses = FormatPasses.All.Where(predicate: pass => (pass.Syntactic && selected.Contains(item: pass.Name))).ToList();

        if (syntacticPasses.Count > 0) {
            result = Math.Max(
                val1: result,
                val2: SourceRewrite.Run(
                    check: check,
                    label: "format",
                    passes: syntacticPasses,
                    rootArgument: root,
                    targets: targets
                )
            );
        }

        return Math.Max(
            val1: result,
            val2: SemanticPhases.Run(
                check: check,
                closures: closures,
                configuration: configuration,
                namedArgs: selected.Contains(item: "named-args"),
                nullPattern: selected.Contains(item: "null-pattern"),
                rootArgument: root,
                targets: targets
            )
        );
    }

    private static int Run(string configuration, string? fileList, string? only, string root, bool check) {
        var selected = Selection(only: only);

        try {
            // A --file-list and its entries resolve against the working directory; a root is formatted as the
            // selection of every source under it, so file-based apps and linked sources are routed exactly as an
            // explicit selection routes them.
            if (fileList is not null) {
                return FormatSelection.Run(
                    check: check,
                    configuration: configuration,
                    manifest: fileList,
                    root: Directory.GetCurrentDirectory(),
                    selected: selected
                );
            }

            // The walk reports a root that names nothing on disk.
            if (FormatSources.Enumerate(root: root) is not { } targets) {
                return CliExit.Refused;
            }

            // A root naming one file formats that file, with its directory as the whitespace formatter's workspace.
            var full = Path.GetFullPath(path: root);

            return FormatSelection.Run(
                check: check,
                configuration: configuration,
                root: (Directory.Exists(path: full)
                    ? full
                    : Path.GetDirectoryName(path: full)!
                ),
                selected: selected,
                targets: [.. targets]
            );
        } catch (Exception error) when ((error is IOException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)) {
            return CliExit.Refuse(
                verb: "format",
                what: (fileList ?? root),
                why: error.Message
            );
        }
    }
    // An absent --only takes the registry's own default set; a supplied one is lowercased so pass names
    // compare ordinally against the registry.
    private static HashSet<string> Selection(string? only) => ((only is null)
        ? FormatPasses.DefaultSelection()
        : only.Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: ','
        )
            .Select(selector: static name => name.ToLowerInvariant())
            .ToHashSet(comparer: StringComparer.Ordinal)
    );

    public static Command Create() {
        var check = CliOptions.Check(description: "Write nothing; exit 1 on any drift, and on a rewrite that would introduce syntax errors or a pass that is not a fixed point.");
        var configuration = CliOptions.Configuration(description: "The build configuration the semantic passes resolve symbols against; its build must already exist.");
        var fileList = CliOptions.FileList(description: "Format only the sources listed in this JSON array of paths; the file and its entries resolve against the working directory.");
        var onlyOption = new Option<string>(name: "--only") { Description = "Run only these comma-separated passes instead of the default set." };
        var rootArgument = new Argument<string>(name: "root") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = static _ => ".", Description = "The tree, or the one source, to format, resolved against the working directory." };
        var command = new Command(
            description: "Format the C# and .puck sources under a tree to Puck's conventions.",
            name: "format"
        ) { rootArgument, check, configuration, fileList, onlyOption };

        command.Detail(detail: $"""
            A .puck source is parsed and printed back in the one layout the language server's
            formatting request also produces; one that does not parse is refused by name and
            left alone. A C# source goes through `dotnet format whitespace` first (phase 0,
            over exactly the selected files, loading no project), then the syntactic passes,
            then the semantic null-pattern and named-args passes, which resolve symbols against
            each project's compile closure as MSBuild reports it for --configuration. A project
            not built in that configuration has its files skipped and named in every mode. A
            pass whose input has syntax errors, or whose output would add them, is dropped and
            reported, never written. Without --check the run writes: run --check first on a
            tree nobody has swept.

            The tree walk skips generated and vendored directories (bin, obj, artifacts, ...)
            and the quarantined experimental trees. --file-list replaces <root>; the two do
            not combine.

            Passes, in the order they run:
              {FormatPasses.Names}
            The default set omits member-groups and the vertical line-wrappers
            logical-lines, arg-lines and ternary-lines.

            Exit codes: 0 clean or written; 1 drift under --check, or a skipped rewrite;
            2 a missing root, an unreadable or unparseable source, or a tool failure.
            """);
        command.Validators.Add(item: result => {
            if (
                (result.GetValue(option: fileList) is not null) &&
                (result.GetResult(argument: rootArgument) is { Tokens.Count: > 0 })
            ) {
                result.AddError(errorMessage: "--file-list replaces <root>: its entries resolve against the working directory, so name no root with it.");

                return;
            }

            if (result.GetValue(option: onlyOption) is not { } only) {
                return;
            }

            var selected = Selection(only: only);

            // An empty or all-separator value selects nothing, and a run that rewrites nothing reads
            // exactly like a tree that is already clean. The caller meant something; say what exists.
            if (selected.Count == 0) {
                result.AddError(errorMessage: $"--only named no format pass (known: {FormatPasses.Names}).");

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
            check: parseResult.GetValue(option: check),
            configuration: parseResult.GetRequiredValue(option: configuration),
            fileList: parseResult.GetValue(option: fileList),
            only: parseResult.GetValue(option: onlyOption),
            root: parseResult.GetRequiredValue(argument: rootArgument)
        ));
        return command;
    }
}
