using System.CommandLine;
using System.Text;

namespace Puck.Cli.Formats;

/// <summary><c>puck formats</c>: writes <c>FormatVersions.json</c> from the source, or under <c>--check</c> fails when
/// the checked-in ledger disagrees with it.</summary>
public static class FormatsCommand {
    private const string Verb = "formats";

    private static string[] ListSources(string repositoryRoot, params string[] options) {
        var listing = CliGit.Run(
            repository: repositoryRoot,
            arguments: ["ls-files", "-z", .. options, "--", "src/*.cs"]
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
        )).Order(comparer: StringComparer.Ordinal).ToArray();
    }

    /// <summary>Reads tracked and unignored new source files so staging cannot change the recorded shape.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>Each file's text by repository-relative path with forward slashes.</returns>
    internal static Dictionary<string, string> ReadSources(string repositoryRoot) {
        var files = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var relative in ListSources(repositoryRoot, "--cached", "--others", "--exclude-standard")) {
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

    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        return Execute(check: check, repositoryRoot: repositoryRoot);
    }

    /// <summary>Records or checks the tracked-source ledger, refusing before discovery when untracked sources
    /// would be omitted.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="check">Whether to check the ledger without writing it.</param>
    /// <returns>Zero on success, one for ledger drift, or two for untracked sources or an unusable ledger.</returns>
    public static int Execute(string repositoryRoot, bool check) {
        var untracked = ListSources(repositoryRoot, "--others", "--exclude-standard");

        if (untracked.Length != 0) {
            foreach (var relative in untracked) {
                Console.Error.WriteLine(value: $"{Verb}: untracked source: {relative}");
            }

            return CliExit.Refuse(
                verb: Verb,
                what: FormatVersionsLedger.FileName,
                why: "git add or remove the untracked sources listed above; the ledger is computed from tracked sources only and cannot describe what will be committed."
            );
        }

        var current = FormatVersionsLedger.Discover(files: ReadSources(repositoryRoot: repositoryRoot));
        var path = Path.Combine(
            path1: repositoryRoot,
            path2: FormatVersionsLedger.FileName
        );

        if (!check) {
            File.WriteAllText(
                contents: FormatVersionsLedger.Render(entries: current),
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                path: path
            );
            Console.WriteLine(value: $"{Verb}: wrote {FormatVersionsLedger.FileName} — {current.Count} format(s).");

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
        );

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
            Records every strictly versioned wire, persisted, or cache format in tracked and
            unignored new source under src/: a static constant or read-only field whose initializer is a
            named document schema (a string like "puck.world.definition.v1"), or whose name is
            a recognized token member (WireKey, ProtocolKey, ShapeToken, SupportedVersion,
            CurrentVersion, FormatVersion, Magic, and kin) over exactly one literal. A numeric
            token that is a four- to eight-character code is spelled as text (the key
            0x354445464B435550 is PUCKFED5).

            Each entry carries its token, the file declaring it and a digest of canonical
            syntax of that file, its partial siblings and its data and codec dependencies, so
            two branches that change one format still conflict on the digest line even
            when its token stays fixed. A codec edit fails the check until its author
            records the new digest. Tokens stay fixed until release.
            Formatting, comments and local renames preserve the digest. Operator grouping,
            argument binding, evaluation order and serialized member names remain significant.

            Both recording and --check refuse before discovery if non-ignored, untracked C#
            sources exist under src/, excluding .g.cs files. The refusal names every file;
            git add or remove them first. The ledger is computed from tracked sources only
            and cannot describe what will be committed while those sources are omitted.

            --check writes nothing and exits 1 for an unrecorded, stale, bumped, reshaped, or
            moved format, or a ledger whose bytes differ from what the verb writes. The
            constants stay the source of truth: record deliberate shape changes with
            `puck formats`, keeping tokens fixed until release.

            Exit codes: 0 written, or the ledger holds; 1 drift under --check; 2 an unusable
            or missing ledger, or untracked sources.
            """);

        return command;
    }
}
