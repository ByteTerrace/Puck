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
    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
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
            Records every strictly versioned wire, persisted, or cache format in the tracked
            source under src/: a static constant or read-only field whose initializer is a
            named document schema (a string like "puck.world.definition.v1"), or whose name is
            a recognized token member (WireKey, ProtocolKey, ShapeToken, SupportedVersion,
            CurrentVersion, FormatVersion, Magic, and kin) over exactly one literal. A numeric
            token that is a four- to eight-character code is spelled as text (the key
            0x354445464B435550 is PUCKFED5).

            Each entry carries its token and the file declaring it. A binary format also
            carries a digest of the non-trivia tokens of that file and its partial siblings, so
            two branches that bump one format to the same new token still conflict on the
            digest line, and a codec edited without a bump fails the check until its author
            records the new digest and decides whether the token should move.

            --check writes nothing and exits 1 for an unrecorded, stale, bumped, reshaped, or
            moved format, or a ledger whose bytes differ from what the verb writes. The
            constants stay the source of truth: bump one, then run `puck formats`.

            Exit codes: 0 written, or the ledger holds; 1 drift under --check; 2 an unusable
            or missing ledger.
            """);

        return command;
    }
}
