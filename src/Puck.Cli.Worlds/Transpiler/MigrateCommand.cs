using Puck.Confinement;
using System.CommandLine;
using System.Text.Json.Nodes;
using Puck.Cli.Format;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Rewriting;
using Puck.World.Transpiler;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck migrate</c> — applies one named syntax-tree rewrite to every <c>.puck</c> source under a path
/// and prints each result through the same printer <c>puck format</c> uses.</summary>
/// <remarks>
/// <para>The run is all-or-nothing: every source is planned, and each source the run would change is compiled
/// before and after and judged against the migration's own declared members, before anything is written — so a
/// refusal at plan time leaves the whole tree as it was.</para>
/// <para>Each source is written by staging its rewrite in a sibling <c>&lt;name&gt;.&lt;random&gt;.tmp</c> file (<c>AtomicFile</c>) and moving that
/// over the destination, so one file is never left partly written. The one thing that is not atomic is the SET of
/// files: if a move fails after earlier moves succeeded, those destinations stay migrated, and the refusal names
/// each of them. A destination that cannot be opened for writing is refused at plan time instead, so the common
/// causes — a read-only file, a lock, a permission — never reach the move phase.</para>
/// </remarks>
public static class PuckMigrateCommand {
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
        var staged = new List<(Plan Plan, AtomicFile.PendingWrite Write)>(capacity: changed.Count);

        foreach (var plan in changed) {
            try {
                staged.Add(item: (plan, AtomicFile.StageAllText(path: plan.Path, contents: plan.Rewritten!)));
            } catch (Exception ex) {
                Console.Error.WriteLine(value: $"error: {plan.Path}: could not be staged for writing: {ex.Message}");
                Discard(writes: staged);
                Console.Error.WriteLine(value: $"migrate: '{migration.Name}' could not stage every rewrite; nothing was written.");

                return 2;
            }
        }

        var replaced = new List<string>(capacity: staged.Count);

        foreach (var (plan, pending) in staged) {
            try {
                pending.Commit();
            } catch (Exception ex) {
                Console.Error.WriteLine(value: $"error: {plan.Path}: could not be replaced: {ex.Message}");
                Discard(writes: staged.Skip(count: replaced.Count));

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
    // A migration whose rewrite of one source changes what another compiles to: every source is compiled before, every
    // rewrite is written, every source that compiled is compiled again against the others' rewrites and held to the
    // migration's own verdict, and every source is restored when one fails it.
    private static int Across(PuckMigration migration, IReadOnlyList<string> sources, bool check, bool keep) {
        var originals = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var parsed = new List<MigrationSource>(capacity: sources.Count);
        var refused = 0;

        foreach (var path in sources) {
            string text;

            try {
                text = File.ReadAllText(path: path);
            } catch (Exception ex) {
                Console.Error.WriteLine(value: $"error: {path}: could not be read: {ex.Message}");
                refused++;
                continue;
            }

            var vocabulary = CliVocabularyResolver.Instance.Resolve(source: text);
            var parse = PuckParser.ParseDocumentWithDiagnostics(source: text, vocabulary: vocabulary);

            if (parse.Value is not { } document) {
                Console.Error.WriteLine(value: $"error: {path}: does not parse, so it cannot be migrated: {Summarize(diagnostics: parse.Diagnostics)}");
                refused++;
                continue;
            }

            var map = new SourceMap();
            var diagnostics = new DiagnosticBag();
            var (_, json) = CompileCommand.CompileSource(diagnostics: diagnostics, imports: ImportHandling.Validate, sourceMap: map, sourcePath: path, sourceText: text, vocabulary: vocabulary);
            var before = ((diagnostics.HasErrors || (json is null) || (json.Count == 0)) ? null : json);

            originals[path] = text;
            parsed.Add(item: new MigrationSource(Before: before, Document: document, Map: ((before is null) ? null : map), Path: path));
        }

        if (refused != 0) {
            Console.Error.WriteLine(value: $"migrate: {refused} source(s) refused '{migration.Name}'; nothing was written.");

            return 2;
        }

        migration.Prepare(sources: parsed);

        if (migration.Refusals.Count != 0) {
            foreach (var refusal in migration.Refusals) { Console.Error.WriteLine(value: $"error: {refusal}"); }
            Console.Error.WriteLine(value: $"migrate: '{migration.Name}' refused {migration.Refusals.Count} source construct(s); nothing was written.");

            return 2;
        }

        var changed = new List<Plan>();

        foreach (var source in parsed) {
            string? printed;

            try {
                printed = migration.ApplyText(document: source.Document, path: source.Path, source: originals[source.Path]);
            } catch (PuckRewriteException ex) {
                Console.Error.WriteLine(value: $"error: {source.Path}: migration '{migration.Name}' could not rewrite it: {ex.Message}");
                return 2;
            }
            if ((printed is null) || string.Equals(a: printed, b: originals[source.Path], comparisonType: StringComparison.Ordinal)) { continue; }

            var reparsed = PuckParser.ParseDocumentWithDiagnostics(source: printed, vocabulary: CliVocabularyResolver.Instance.Resolve(source: printed));

            if ((reparsed.Value is null) || reparsed.Diagnostics.Any(predicate: static d => (d.Severity == DiagnosticSeverity.Error))) {
                Console.Error.WriteLine(value: $"error: {source.Path}: migration '{migration.Name}' produced a source that does not parse back: {Summarize(diagnostics: reparsed.Diagnostics)}");
                foreach (var diagnostic in reparsed.Diagnostics.Where(predicate: static d => (d.Severity == DiagnosticSeverity.Error)).Take(count: 3)) {
                    var lines = printed.Split(separator: '\n');

                    for (var line = Math.Max(val1: 1, val2: (diagnostic.Span.Line - 3)); (line <= Math.Min(val1: lines.Length, val2: diagnostic.Span.Line)); line++) {
                        Console.Error.WriteLine(value: $"  {line}: {lines[line - 1]}");
                    }
                }
                return 2;
            }

            changed.Add(item: new Plan(Path: source.Path, Refusal: null, Rewritten: printed));
        }

        if (check) {
            foreach (var plan in changed) { Console.WriteLine(value: $"would migrate: {plan.Path}"); }
            if (changed.Count != 0) {
                Console.Error.WriteLine(value: $"migrate: {changed.Count} source(s) would change under '{migration.Name}'.");
                return 1;
            }

            return 0;
        }

        foreach (var plan in changed) {
            if (Unwritable(path: plan.Path) is { } reason) {
                Console.Error.WriteLine(value: $"error: {plan.Path}: would change but cannot be replaced: {reason}");
                return 2;
            }
        }

        if (Write(changed: changed, migration: migration, planned: parsed.Count) != 0) {
            Restore(originals: originals, paths: changed.Select(selector: static plan => plan.Path));
            return 2;
        }

        var failures = 0;

        try {
            foreach (var source in parsed) {
                if (source.Before is null) { continue; }

                var text = File.ReadAllText(path: source.Path);
                var diagnostics = new DiagnosticBag();
                var (_, after) = CompileCommand.CompileSource(diagnostics: diagnostics, imports: ImportHandling.Validate, sourceMap: new SourceMap(), sourcePath: source.Path, sourceText: text, vocabulary: CliVocabularyResolver.Instance.Resolve(source: text));

                if (diagnostics.HasErrors || (after is null)) {
                    Console.Error.WriteLine(value: $"error: {source.Path}: does not compile after migration '{migration.Name}': {Summarize(diagnostics: diagnostics)}");
                    failures++;
                } else if (migration.DocumentDifference(after: after, before: source.Before) is { } difference) {
                    Console.Error.WriteLine(value: $"error: {source.Path}: migration '{migration.Name}' changed what it compiles to — {difference}");
                    failures++;
                }
            }
        } catch {
            Restore(originals: originals, paths: changed.Select(selector: static plan => plan.Path));
            throw;
        }

        if (failures != 0) {
            if (keep) {
                Console.Error.WriteLine(value: $"migrate: {failures} source(s) failed '{migration.Name}''s proof; the rewritten sources were kept.");

                return 2;
            }

            Restore(originals: originals, paths: changed.Select(selector: static plan => plan.Path));
            Console.Error.WriteLine(value: $"migrate: {failures} source(s) failed '{migration.Name}''s proof; every source was restored.");

            return 2;
        }

        return 0;
    }
    private static void Restore(Dictionary<string, string> originals, IEnumerable<string> paths) {
        foreach (var path in paths) {
            using var pending = AtomicFile.StageAllText(path: path, contents: originals[path]);

            pending.Commit();
        }
    }
    private static void Discard(IEnumerable<(Plan Plan, AtomicFile.PendingWrite Write)> writes) {
        foreach (var (_, pending) in writes) { pending.Dispose(); }
    }

    public static Command Create() {
        var nameArgument = new Argument<string>(name: "name") { Description = "The registered migration to apply; an unknown name lists the registered ones." };
        var pathArgument = new Argument<string>(name: "path") { Description = "The directory whose .puck sources are migrated, or one .puck file." };
        var checkOption = new Option<bool>(name: "--check") { Description = "Report what the migration would change without writing it (exit code 1 when anything would change)." };
        var keepOption = new Option<bool>(name: "--keep") { Description = "For a migration proven across sources, keep the rewritten sources when the proof fails, so its failures can be read and finished by hand." };

        var command = new Command(
            description: "Apply one named syntax-tree rewrite to every .puck source under a path, proving each rewritten source compiles to the document it did before apart from the members the migration declares.",
            name: "migrate"
        ) {
            nameArgument,
            pathArgument,
            checkOption,
            keepOption,
        };

        command.SetAction(action: parseResult => Execute(
            check: parseResult.GetValue(option: checkOption),
            keep: parseResult.GetValue(option: keepOption),
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
    /// <param name="keep">Whether a migration proven across sources keeps its rewritten sources when the proof
    /// fails.</param>
    public static int Execute(string name, string path, bool check, IReadOnlyList<PuckMigration>? migrations = null, bool keep = false) {
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

        if (migration.ProvesAcrossSources) {
            return Across(check: check, keep: keep, migration: migration, sources: Sources(fullPath: fullPath));
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
