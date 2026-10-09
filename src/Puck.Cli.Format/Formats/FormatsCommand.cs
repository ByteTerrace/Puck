using System.CommandLine;
using System.Text;

namespace Puck.Cli.Formats;

/// <summary><c>puck formats</c>: writes <c>FormatVersions.json</c> from the source, or under <c>--check</c> fails when
/// the checked-in ledger disagrees with it.</summary>
public static class FormatsCommand {
    private const string Verb = "formats";

    private static string[] ListSources(string repositoryRoot, string pathspec) {
        var listing = CliGit.Run(
            repository: repositoryRoot,
            arguments: ["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", pathspec]
        );

        if (listing.ExitCode != 0) {
            throw new InvalidOperationException(message: $"git ls-files failed: {listing.Stderr}");
        }

        return listing.Stdout.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        ).Where(predicate: static relative => !relative.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".g.cs"
        )).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray();
    }

    /// <summary>Reads tracked and unignored new source files, so staging cannot change the recorded shape, in the compile
    /// context their projects give them: every tracked or unignored new project under <c>src/</c> is evaluated for its
    /// usings and the files it links in (<see cref="FormatShapeSources.Evaluate"/>).</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>Each file's text by repository-relative path with forward slashes, with its compile context.</returns>
    public static FormatShapeSources ReadSources(string repositoryRoot) => FormatShapeSources.Evaluate(
        files: ReadFiles(repositoryRoot: repositoryRoot),
        projects: [.. ListSources(pathspec: "src/*.csproj", repositoryRoot: repositoryRoot).Where(predicate: relative => File.Exists(path: Path.Combine(path1: repositoryRoot, path2: relative)))],
        repositoryRoot: repositoryRoot
    );

    private static Dictionary<string, string> ReadFiles(string repositoryRoot) {
        var files = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var relative in ListSources(pathspec: "src/*.cs", repositoryRoot: repositoryRoot)) {
            var fullPath = Path.Combine(
                path1: repositoryRoot,
                path2: relative
            );

            if (!File.Exists(path: fullPath)) {
                continue;
            }

            files[relative] = File.ReadAllText(path: fullPath);
        }

        return files;
    }

    /// <summary>Plans every generated <c>FormatShapes.g.cs</c> for a checkout.</summary>
    /// <param name="entries">The ledger's entries.</param>
    /// <param name="repositoryRoot">The repository root, whose project files place each constants file.</param>
    /// <param name="sources">Every tracked or unignored new source file's text, by repository-relative path.</param>
    /// <returns>Each file's repository-relative path and its text.</returns>
    public static IReadOnlyDictionary<string, string> ShapeFiles(IReadOnlyList<FormatEntry> entries, string repositoryRoot, IReadOnlyDictionary<string, string> sources) => FormatShapesFiles.Plan(
        entries: entries,
        projectOf: source => ProjectOf(
            repositoryRoot: repositoryRoot,
            source: source,
            sources: sources
        )
    );

    // The project a declaring file belongs to (the nearest directory above it holding a project file) and the namespace
    // the file itself declares: the generated constants live there, beside the codec that reads them.
    private static (string Directory, string Namespace) ProjectOf(string repositoryRoot, IReadOnlyDictionary<string, string> sources, string source) {
        var declared = System.Text.RegularExpressions.Regex.Match(
            input: sources[source],
            options: System.Text.RegularExpressions.RegexOptions.Multiline,
            pattern: @"^namespace\s+([A-Za-z0-9_.]+)\s*[;{]"
        );

        if (!declared.Success) {
            throw new InvalidOperationException(message: $"'{source}' declares a format but no namespace.");
        }

        var directory = source[..source.LastIndexOf(value: '/')];

        while (directory.Length > 0) {
            var full = Path.Combine(
                path1: repositoryRoot,
                path2: directory
            );

            if (Directory.EnumerateFiles(
                path: full,
                searchOption: SearchOption.TopDirectoryOnly,
                searchPattern: "*.csproj"
            ).Any()) {
                return ($"{directory}/", declared.Groups[1].Value);
            }

            var cut = directory.LastIndexOf(value: '/');

            directory = ((cut < 0)
                ? string.Empty
                : directory[..cut]);
        }

        throw new InvalidOperationException(message: $"'{source}' has no project file above it.");
    }
    // Every FormatShapes.g.cs the checkout tracks, plus any a plan would write that already exists on disk.
    private static Dictionary<string, string> ExistingShapes(string repositoryRoot, IReadOnlyDictionary<string, string> plan) {
        var listing = CliGit.Run(
            repositoryRoot,
            "ls-files", "-z", "--", $"src/*{FormatShapesFiles.FileName}"
        );

        if (listing.ExitCode != 0) {
            throw new InvalidOperationException(message: $"git ls-files failed: {listing.Stderr}");
        }

        var paths = listing.Stdout.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        ).Concat(second: plan.Keys).Distinct(comparer: StringComparer.Ordinal);
        var existing = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var relative in paths) {
            var full = Path.Combine(
                path1: repositoryRoot,
                path2: relative
            );

            if (File.Exists(path: full)) {
                existing[relative] = File.ReadAllText(path: full);
            }
        }

        return existing;
    }
    private static int Explain(string id) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        try {
            if (FormatVersionsLedger.Explain(
                sources: ReadSources(repositoryRoot: repositoryRoot),
                id: id
            ) is not var (entry, closure)) {
                return CliExit.Refuse(
                    verb: Verb,
                    what: id,
                    why: "no format has this id; see FormatVersions.json."
                );
            }

            Console.WriteLine(value: $"{entry.Id} ({entry.Source}) token {entry.Token} shape {entry.Shape}: {closure.Units.Count} unit(s) in {closure.Units.Select(selector: static unit => unit.Path).Distinct(comparer: StringComparer.Ordinal).Count()} file(s), {closure.Open.Count} open call(s), {closure.Outside.Count} name(s) outside the repository.");

            foreach (var group in closure.Units.GroupBy(keySelector: static unit => unit.Path, comparer: StringComparer.Ordinal).OrderBy(keySelector: static group => group.Key, comparer: StringComparer.Ordinal)) {
                Console.WriteLine(value: $"  {group.Key}");

                foreach (var unit in group.OrderBy(keySelector: static unit => unit.Key, comparer: StringComparer.Ordinal)) { Console.WriteLine(value: $"    {unit.Kind.ToString().ToLowerInvariant()} {unit.Key}"); }
            }
            foreach (var call in closure.Open) { Console.WriteLine(value: $"  open {call}"); }
            foreach (var name in closure.Outside) { Console.WriteLine(value: $"  outside {name}"); }

            return CliExit.Success;
        } catch (FormatBoundaryException exception) {
            foreach (var problem in exception.Problems) { Console.Error.WriteLine(value: $"{Verb}: {problem}"); }

            return CliExit.Failed;
        } catch (Exception exception) when ((exception is InvalidOperationException or TimeoutException)) {
            return Unevaluated(exception: exception);
        }
    }
    // The projects could not be evaluated, so no file has its compile context and no shape can be computed honestly.
    private static int Unevaluated(Exception exception) => CliExit.Refuse(
        verb: Verb,
        what: "the projects under src/",
        why: exception.Message
    );
    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        return Execute(check: check, repositoryRoot: repositoryRoot);
    }

    /// <summary>Records or checks the ledger over tracked and unignored new sources.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="check">Whether to check the ledger without writing it.</param>
    /// <returns>Zero on success, one for ledger drift, or two for an unusable ledger.</returns>
    public static int Execute(string repositoryRoot, bool check) {
        FormatShapeSources sources;
        IReadOnlyList<FormatEntry> current;

        try {
            sources = ReadSources(repositoryRoot: repositoryRoot);
        } catch (Exception exception) when ((exception is InvalidOperationException or TimeoutException)) {
            return Unevaluated(exception: exception);
        }
        try {
            current = FormatVersionsLedger.Discover(sources: sources);
        } catch (FormatBoundaryException exception) {
            foreach (var problem in exception.Problems) { Console.Error.WriteLine(value: $"{Verb}: {problem}"); }

            return CliExit.Failed;
        }

        var path = Path.Combine(
            path1: repositoryRoot,
            path2: FormatVersionsLedger.FileName
        );
        var shapes = ShapeFiles(
            entries: current,
            repositoryRoot: repositoryRoot,
            sources: sources.Files
        );
        var existing = ExistingShapes(
            plan: shapes,
            repositoryRoot: repositoryRoot
        );

        if (!check) {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            File.WriteAllText(
                contents: FormatVersionsLedger.Render(entries: current),
                encoding: utf8,
                path: path
            );

            foreach (var (relative, shapeText) in shapes) {
                File.WriteAllText(
                    contents: shapeText,
                    encoding: utf8,
                    path: Path.Combine(
                        path1: repositoryRoot,
                        path2: relative
                    )
                );
            }
            foreach (var stale in existing.Keys.Where(predicate: relative => !shapes.ContainsKey(key: relative))) {
                File.Delete(path: Path.Combine(
                    path1: repositoryRoot,
                    path2: stale
                ));
            }

            Console.WriteLine(value: $"{Verb}: wrote {FormatVersionsLedger.FileName} — {current.Count} format(s) — and {shapes.Count} {FormatShapesFiles.FileName} file(s).");

            return CliExit.Success;
        }
        if (!File.Exists(path: path)) {
            return CliExit.Refuse(
                verb: Verb,
                what: FormatVersionsLedger.FileName,
                why: $"'{path}' does not exist; run 'puck {Verb}' to record it."
            );
        }

        var text = File.ReadAllText(path: path);

        if (!FormatVersionsLedger.TryParse(
            entries: out var recorded,
            error: out var error,
            json: text
        )) {
            return CliExit.Refuse(
                verb: Verb,
                what: FormatVersionsLedger.FileName,
                why: $"unusable: {error}"
            );
        }

        var problems = FormatVersionsLedger.Check(
            current: current,
            recorded: recorded,
            recordedText: text
        ).Concat(second: FormatShapesFiles.Check(
            existing: existing,
            plan: shapes
        )).ToList();

        foreach (var problem in problems) {
            Console.Error.WriteLine(value: $"{Verb}: {problem}");
        }

        if (problems.Count != 0) {
            Console.Error.WriteLine(value: $"{Verb}: run 'puck {Verb}' and commit {FormatVersionsLedger.FileName}.");
        }

        Console.WriteLine(value: $"{Verb}: {current.Count} format(s); {problems.Count} problem(s).");

        return ((problems.Count == 0)
            ? CliExit.Success
            : CliExit.Failed);
    }
    /// <summary>Creates <c>puck formats</c>.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var command = CliOptions.CheckVerb(
            checkDescription: "Write nothing; exit 1 when FormatVersions.json disagrees with the source, naming each format.",
            description: "Regenerate FormatVersions.json, the ledger of strictly versioned formats, from the source.",
            name: Verb,
            run: Run
        );

        var explain = new Option<string?>(name: "--explain") { Description = "Write nothing; print what one format's shape covers (units by file) and the calls it leaves open." };

        command.Add(option: explain);
        command.SetAction(action: parseResult => ((parseResult.GetValue(option: explain) is { } id)
            ? Explain(id: id)
            : Run(check: parseResult.GetValue(option: ((Option<bool>)command.Options.First(predicate: static option => (option.Name == "--check")))))));
        command.Detail(detail: """
            Records every strictly versioned wire, persisted, or cache format in tracked and
            unignored new source under src/: a static constant or read-only field whose initializer is a
            named document schema (a string like "puck.world.definition.v1"), or whose name is
            a recognized token member (WireKey, ProtocolKey, ShapeToken, SupportedVersion,
            CurrentVersion, FormatVersion, Magic, and kin) over exactly one literal. A numeric
            token that is a four- to eight-character code is spelled as text (the key
            0x354445464B435550 is PUCKFED5).

            Each entry carries its token, the file declaring it and a digest of the canonical
            syntax of its closure: every unit of the declaring file, its partial siblings and each
            unit naming the token, the enums and constants they read, the types they name one
            level deep, and the members they call that are marked [FormatLeaf]. A call into any
            other repository member is open: it is recorded in the entry, and the check reports drift,
            and [FormatSeam("its behaviour sets no byte because ...")] declares one outside the
            wire. Two branches that bump one format to the same new token still conflict on the
            digest line. Formatting, comments and local renames preserve the digest. Operator
            grouping, argument binding, evaluation order and serialized member names remain
            significant.

            Both recording and --check read tracked and non-ignored new C# sources under
            src/, excluding .g.cs files. Staging a source does not change its shape. A new
            codec participates before staging; --check reports its missing entry. Each source
            compiles with its own project's usings, which an MSBuild evaluation of every project
            under src/ reads without building or restoring; a closure that names a repository
            type in its project's scope and cannot bind it is refused.

            Each project that declares a format also gets a generated FormatShapes.g.cs holding
            one constant per entry, that entry's shape digest, so the codec that owns the format
            writes it in its header or handshake and refuses any other value by name. A token
            says nothing the fingerprint does not; no edit demands a bump.

            --check writes nothing and exits 1 for an unrecorded, stale, retokened, reshaped, or
            moved format, a ledger whose bytes differ from what the verb writes, or a
            FormatShapes.g.cs that disagrees with it. It records the shape; it never demands a
            bump: edit the source, then run `puck formats`.

            Exit codes: 0 written, or the ledger holds; 1 drift under --check; 2 an unusable
            or missing ledger.
            """);

        return command;
    }
}
