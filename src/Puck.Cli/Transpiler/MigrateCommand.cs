using System.CommandLine;
using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Rewriting;
using Puck.World.Transpiler;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck migrate</c> — applies one named syntax-tree rewrite to every <c>.puck</c> source under a path
/// and prints each result through the same printer <c>puck fmt</c> uses.</summary>
/// <remarks>
/// <para>The run is all-or-nothing: every source is planned, and each source the run would change is compiled
/// before and after and judged against the migration's own declared members, before anything is written — so a
/// refusal at plan time leaves the whole tree as it was.</para>
/// <para>Each source is written by staging its rewrite in a sibling <c>.migrate-tmp</c> file and moving that over
/// the destination, so one file is never left partly written. The one thing that is not atomic is the SET of
/// files: if a move fails after earlier moves succeeded, those destinations stay migrated, and the refusal names
/// each of them. A destination that cannot be opened for writing is refused at plan time instead, so the common
/// causes — a read-only file, a lock, a permission — never reach the move phase.</para>
/// </remarks>
internal static class PuckMigrateCommand {
    // What one source's plan came to. `Rewritten` is null when the source needs no change; `Refusal` is null when
    // the plan holds.
    private sealed record Plan(string Path, string? Rewritten, string? Refusal);

    private static JsonObject? Compile(string sourceText, string sourcePath, DiagnosticBag diagnostics) {
        var (_, json) = CompileCommand.CompileSource(
            diagnostics: diagnostics,
            imports: ImportHandling.Validate,
            sourceMap: new SourceMap(),
            sourcePath: sourcePath,
            sourceText: sourceText,
            vocabulary: CliVocabularyResolver.Instance.Resolve(source: sourceText)
        );

        return (diagnostics.HasErrors
            ? null
            : json
        );
    }
    private static Plan Planned(string path, PuckMigration migration) {
        string original;

        try {
            original = File.ReadAllText(path: path);
        } catch (Exception ex) {
            return new Plan(
                Path: path,
                Refusal: $"could not be read: {ex.Message}",
                Rewritten: null
            );
        }

        var vocabulary = CliVocabularyResolver.Instance.Resolve(source: original);
        var parsed = PuckParser.ParseDocumentWithDiagnostics(
            source: original,
            vocabulary: vocabulary
        );

        if (parsed.Value is not { } document) {
            return new Plan(
                Path: path,
                Refusal: $"does not parse, so it cannot be migrated: {Summarize(diagnostics: parsed.Diagnostics)}",
                Rewritten: null
            );
        }

        string? printed;

        try {
            printed = PuckPrinter.TryPrint(document: migration.Apply(document: document));
        } catch (PuckRewriteException ex) {
            return new Plan(
                Path: path,
                Refusal: $"migration '{migration.Name}' could not rewrite it: {ex.Message}",
                Rewritten: null
            );
        }

        if (printed is null) {
            return new Plan(
                Path: path,
                Refusal: $"migration '{migration.Name}' produced a tree the printer cannot write back",
                Rewritten: null
            );
        }
        // A source the migration left alone is neither compiled nor written: only a source the run would change
        // owes the before-and-after verdict, so an unrelated source that does not compile cannot stop a sweep it
        // has no part in.
        if (string.Equals(
            a: original,
            b: printed,
            comparisonType: StringComparison.Ordinal
        )) {
            return new Plan(
                Path: path,
                Refusal: null,
                Rewritten: null
            );
        }

        // The comment verdict is read off what the migrated source parses back to, so it covers the printer as
        // well as the rewrite: a comment the printer cannot reproduce is as lost as one the rewrite dropped.
        var reparsed = PuckParser.ParseDocumentWithDiagnostics(
            source: printed,
            vocabulary: vocabulary
        );

        if (reparsed.Value is not { } migrated) {
            return new Plan(
                Path: path,
                Refusal: $"migration '{migration.Name}' produced a source that does not parse back: {Summarize(diagnostics: reparsed.Diagnostics)}",
                Rewritten: null
            );
        }

        if (migration.UndeclaredCommentChange(
            after: migrated,
            before: document
        ) is { } comment) {
            return new Plan(
                Path: path,
                Refusal: $"migration '{migration.Name}' does not declare that it reshapes comments, and {comment}",
                Rewritten: null
            );
        }

        // Asked while the run can still refuse for free. A destination that cannot be opened for writing would
        // fail at its own replacement, after earlier destinations had already been replaced.
        if (Unwritable(path: path) is { } reason) {
            return new Plan(
                Path: path,
                Refusal: $"would change but cannot be replaced, so the run could not stay all-or-nothing: {reason}",
                Rewritten: null
            );
        }

        var beforeDiagnostics = new DiagnosticBag();

        if (Compile(
            diagnostics: beforeDiagnostics,
            sourcePath: path,
            sourceText: original
        ) is not { } before) {
            return new Plan(
                Path: path,
                Refusal: $"does not compile before the migration, so nothing can be compared against: {Summarize(diagnostics: beforeDiagnostics)}",
                Rewritten: null
            );
        }

        var afterDiagnostics = new DiagnosticBag();

        if (Compile(
            diagnostics: afterDiagnostics,
            sourcePath: path,
            sourceText: printed
        ) is not { } after) {
            return new Plan(
                Path: path,
                Refusal: $"does not compile after migration '{migration.Name}': {Summarize(diagnostics: afterDiagnostics)}",
                Rewritten: null
            );
        }

        if (migration.UndeclaredDifference(
            after: after,
            before: before
        ) is { } difference) {
            return new Plan(
                Path: path,
                Refusal: $"migration '{migration.Name}' changed a member it does not declare — {difference}",
                Rewritten: null
            );
        }

        return new Plan(
            Path: path,
            Refusal: null,
            Rewritten: printed
        );
    }
    private static int Report(IReadOnlyList<PuckMigration> migrations, string name) {
        Console.Error.WriteLine(value: $"error: no migration named '{name}'.");

        if (migrations.Count == 0) {
            Console.Error.WriteLine(value: "No migrations are registered: each one lands with the reshape it serves and is deleted once it has run.");

            return 2;
        }

        Console.Error.WriteLine(value: "Registered migrations:");

        foreach (var migration in migrations.OrderBy(keySelector: migration => migration.Name, comparer: StringComparer.Ordinal)) {
            Console.Error.WriteLine(value: $"  {migration.Name}  {migration.Summary}");
        }

        return 2;
    }
    private static IReadOnlyList<string> Sources(string fullPath) => (Directory.Exists(path: fullPath)
        ? [.. Directory.GetFiles(
            path: fullPath,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.puck"
        ).Order(comparer: StringComparer.Ordinal)]
        : [fullPath]
    );
    private static string Summarize(DiagnosticBag diagnostics) => string.Join(
        separator: "; ",
        values: diagnostics
            .Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error))
            .Select(selector: static diagnostic => $"({diagnostic.Span.Line},{diagnostic.Span.Column}) {diagnostic.Code}: {diagnostic.Message}")
    );
    private static string TempPath(string path) => (path + ".migrate-tmp");
    /// <summary>Returns why <paramref name="path"/> could not be opened for writing, or <see langword="null"/>.</summary>
    private static string? Unwritable(string path) {
        try {
            using var handle = new FileStream(
                access: FileAccess.ReadWrite,
                mode: FileMode.Open,
                path: path,
                share: FileShare.ReadWrite
            );

            return null;
        } catch (Exception ex) {
            return ex.Message;
        }
    }
    // Every rewrite is staged beside its destination first, then moved over it: a move on one volume replaces the
    // file in one step, so an interrupted run leaves each source either as it was or fully migrated, never
    // truncated. What no ordering can make atomic is the set: a move that fails after earlier moves succeeded
    // leaves those destinations replaced, which is why the refusal names them.
    private static int Write(IReadOnlyList<Plan> changed, PuckMigration migration, int planned) {
        var staged = new List<Plan>(capacity: changed.Count);

        foreach (var plan in changed) {
            try {
                File.WriteAllText(
                    contents: plan.Rewritten!,
                    path: TempPath(path: plan.Path)
                );
                staged.Add(item: plan);
            } catch (Exception ex) {
                Console.Error.WriteLine(value: $"error: {plan.Path}: could not be staged for writing: {ex.Message}");
                Discard(plans: staged);
                Console.Error.WriteLine(value: $"migrate: '{migration.Name}' could not stage every rewrite; nothing was written.");

                return 2;
            }
        }

        var replaced = new List<string>(capacity: staged.Count);

        foreach (var plan in staged) {
            try {
                File.Move(
                    destFileName: plan.Path,
                    overwrite: true,
                    sourceFileName: TempPath(path: plan.Path)
                );
            } catch (Exception ex) {
                Console.Error.WriteLine(value: $"error: {plan.Path}: could not be replaced: {ex.Message}");
                Discard(plans: staged.Skip(count: replaced.Count));

                if (replaced.Count == 0) {
                    Console.Error.WriteLine(value: $"migrate: '{migration.Name}' replaced nothing; nothing was written.");
                } else {
                    Console.Error.WriteLine(value: $"migrate: '{migration.Name}' had already replaced {replaced.Count} source(s) when it stopped, and those are migrated:");

                    foreach (var path in replaced) {
                        Console.Error.WriteLine(value: $"  {path}");
                    }
                }

                return 2;
            }

            replaced.Add(item: plan.Path);
            Console.WriteLine(value: $"migrated: {plan.Path}");
        }

        Console.WriteLine(value: $"migrate: '{migration.Name}' rewrote {replaced.Count} of {planned} source(s).");

        return 0;
    }
    private static void Discard(IEnumerable<Plan> plans) {
        foreach (var plan in plans) {
            try {
                File.Delete(path: TempPath(path: plan.Path));
            } catch (Exception ex) {
                Console.Error.WriteLine(value: $"warning: {TempPath(path: plan.Path)}: could not be removed: {ex.Message}");
            }
        }
    }

    public static Command Create() {
        var nameArgument = new Argument<string>(name: "name") { Description = "The registered migration to apply; an unknown name lists the registered ones." };
        var pathArgument = new Argument<string>(name: "path") { Description = "The directory whose .puck sources are migrated, or one .puck file." };
        var checkOption = new Option<bool>(
            name: "--check",
            aliases: ["-c"]
        ) { Description = "Report what the migration would change without writing it (exit code 1 when anything would change)." };

        var command = new Command(
            description: "Apply one named syntax-tree rewrite to every .puck source under a path, proving each rewritten source compiles to the document it did before apart from the members the migration declares.",
            name: "migrate"
        ) {
            nameArgument,
            pathArgument,
            checkOption,
        };

        command.SetAction(action: parseResult => Execute(
            check: parseResult.GetValue(option: checkOption),
            name: parseResult.GetRequiredValue(argument: nameArgument),
            path: parseResult.GetRequiredValue(argument: pathArgument)
        ));

        return command;
    }
    /// <summary>Applies <paramref name="name"/>'s migration to every <c>.puck</c> source under
    /// <paramref name="path"/>.</summary>
    /// <param name="name">The registered migration's name.</param>
    /// <param name="path">A directory to walk, or one <c>.puck</c> file.</param>
    /// <param name="check">Whether to report what would change and write nothing.</param>
    /// <param name="migrations">The migrations <paramref name="name"/> is resolved against, or
    /// <see langword="null"/> for the registered set.</param>
    /// <returns>0 when the run held, 1 when <paramref name="check"/> found work to do, 2 on a refusal or a usage
    /// error.</returns>
    /// <remarks>Nothing is written unless every source's plan holds, so a run refused at plan time leaves the tree
    /// untouched. Each write stages a sibling temp file and moves it over its destination, so no single file is
    /// ever left partly written; a move that fails after earlier moves succeeded leaves those destinations
    /// migrated and names them in the refusal.</remarks>
    public static int Execute(string name, string path, bool check, IReadOnlyList<PuckMigration>? migrations = null) {
        var registered = (migrations ?? PuckMigrations.Registered);

        if (PuckMigrations.Resolve(
            migrations: registered,
            name: name
        ) is not { } migration) {
            return Report(
                migrations: registered,
                name: name
            );
        }

        var fullPath = Path.GetFullPath(path: path);

        if (
            !Directory.Exists(path: fullPath) &&
            !File.Exists(path: fullPath)
        ) {
            Console.Error.WriteLine(value: $"error: Path '{path}' not found.");

            return 2;
        }

        var plans = Sources(fullPath: fullPath)
            .Select(selector: source => Planned(
                migration: migration,
                path: source
            ))
            .ToArray();
        var refused = plans.Where(predicate: static plan => (plan.Refusal is not null)).ToArray();

        if (refused.Length != 0) {
            foreach (var plan in refused) {
                Console.Error.WriteLine(value: $"error: {plan.Path}: {plan.Refusal}");
            }

            Console.Error.WriteLine(value: $"migrate: {refused.Length} source(s) refused '{migration.Name}'; nothing was written.");

            return 2;
        }

        var changed = plans.Where(predicate: static plan => (plan.Rewritten is not null)).ToArray();

        if (check) {
            foreach (var plan in changed) {
                Console.WriteLine(value: $"would migrate: {plan.Path}");
            }

            if (changed.Length != 0) {
                Console.Error.WriteLine(value: $"migrate: {changed.Length} source(s) would change under '{migration.Name}'.");

                return 1;
            }

            return 0;
        }

        return Write(
            changed: changed,
            migration: migration,
            planned: plans.Length
        );
    }
}
