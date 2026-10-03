using System.CommandLine;
using System.Text;

namespace Puck.Cli.Formats;

/// <summary><c>puck formats</c>: writes <c>FormatVersions.json</c> from the source, or under <c>--check</c> fails when
/// the checked-in ledger disagrees with it.</summary>
internal static class FormatsCommand {
    private const string Verb = "formats";

    /// <summary>Reads every tracked source file the ledger is generated from.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>Each file's text by repository-relative path with forward slashes.</returns>
    internal static Dictionary<string, string> ReadSources(string repositoryRoot) {
        var listing = CliGit.Run(
            repositoryRoot,
            "ls-files", "-z", "--", "src/*.cs"
        );

        if (listing.ExitCode != 0) {
            throw new InvalidOperationException(message: $"git ls-files failed: {listing.Stderr}");
        }

        var files = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var relative in listing.Stdout.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        )) {
            var fullPath = Path.Combine(
                path1: repositoryRoot,
                path2: relative
            );

            if (
                relative.EndsWith(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: ".g.cs"
                ) ||
                !File.Exists(path: fullPath)
            ) {
                continue;
            }

            files[relative] = File.ReadAllText(path: fullPath);
        }

        return files;
    }
    /// <summary>Plans every generated <c>FormatShapes.g.cs</c> for a checkout.</summary>
    /// <param name="entries">The ledger's entries.</param>
    /// <param name="repositoryRoot">The repository root, whose project files place each constants file.</param>
    /// <param name="sources">Every tracked source file's text, by repository-relative path.</param>
    /// <returns>Each file's repository-relative path and its text.</returns>
    internal static IReadOnlyDictionary<string, string> ShapeFiles(IReadOnlyList<FormatEntry> entries, string repositoryRoot, IReadOnlyDictionary<string, string> sources) => FormatShapesFiles.Plan(
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
    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        var sources = ReadSources(repositoryRoot: repositoryRoot);
        var current = FormatVersionsLedger.Discover(files: sources);
        var path = Path.Combine(
            path1: repositoryRoot,
            path2: FormatVersionsLedger.FileName
        );
        var shapes = ShapeFiles(
            entries: current,
            repositoryRoot: repositoryRoot,
            sources: sources
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

        command.Detail(detail: """
            Records every strictly versioned wire, persisted, or cache format in the tracked
            source under src/: a static constant or read-only field whose initializer is a
            named document schema (a string like "puck.world.definition.v1"), or whose name is
            a recognized token member (WireKey, ProtocolKey, ShapeToken, SupportedVersion,
            CurrentVersion, FormatVersion, Magic, and kin) over exactly one literal. A numeric
            token that is a four- to eight-character code is spelled as text (the key
            0x354445464B435550 is PUCKFED5).

            Each entry carries its token, the file declaring it and a digest of canonical
            syntax of that file, its partial siblings and its data and codec dependencies, so
            two branches that bump one format to the same new token still conflict on the
            digest line, and a codec edited without a bump fails the check until its author
            records the new digest and decides whether the token should move.
            Formatting, comments and local renames preserve the digest. Operator grouping,
            argument binding, evaluation order and serialized member names remain significant.

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
