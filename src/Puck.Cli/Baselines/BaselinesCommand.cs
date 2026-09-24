using System.CommandLine;

namespace Puck.Cli.Baselines;

/// <summary>
/// <c>puck baselines &lt;artifact&gt;</c>: records a committed test baseline from a fresh run of the test that holds
/// it, and <c>--check</c> compares instead. A test never writes the checkout: every run writes the fresh copy of each
/// record it compares against under <c>records/&lt;artifact&gt;</c> beside its assembly (<c>TestRecords</c> in
/// <c>tests/Shared</c>, whose directory and artifact names are the spellings this verb shares with it). This verb
/// builds the test project, clears that directory, runs the owning tests, and then either promotes the records over
/// the committed files or reports every committed file that differs. The test's own verdict does not stop a record:
/// a stale baseline is exactly the failure a recording exists to replace, so the run is refused only when it could
/// not produce every record.
/// </summary>
internal static class BaselinesCommand {
    private const string Verb = "baselines";

    private static readonly TimeSpan BuildBudget = TimeSpan.FromMinutes(value: 15);
    private static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(value: 30);

    /// <summary>Every recorded artifact, in the order <c>--help</c> lists them.</summary>
    internal static IReadOnlyList<BaselineArtifact> Artifacts { get; } = [
        new(
            CheckedFiles: null,
            CommittedDirectory: "tests/Puck.World.Browser.Tests/Fixtures/browser-parity",
            Description: "The browser determinism canary's native state hashes, which the wasm harness also reads.",
            Name: "browser-parity",
            Project: "Puck.World.Browser.Tests",
            RunArguments: ["-class", "Puck.World.Browser.Tests.BrowserParityRecordingTests"],
            Runs: 1
        ),
        new(
            CheckedFiles: null,
            CommittedDirectory: "tests/Puck.State.Rebuild.Corpus",
            Description: "The author-expression corpus inventory, inventory.md.",
            Name: "corpus-inventory",
            Project: "Puck.State.Rebuild.Corpus",
            RunArguments: ["-class", "Puck.State.Rebuild.Corpus.CorpusInventoryTests"],
            Runs: 1
        ),
        // The Default tier, unfiltered, as the ledger has always been recorded; frontier.json and RESULTS.md are run
        // records that move on every green run, so a check holds only the two artifacts the declarations generate.
        new(
            CheckedFiles: ["coverage-manifest.json", "leg-ledger.md"],
            CommittedDirectory: "tests/Puck.Maths.Tests",
            Description: "The Puck.Maths law ledger: coverage manifest, leg ledger, frontier and RESULTS.md.",
            Name: "maths-ledger",
            Project: "Puck.Maths.Tests",
            RunArguments: ["-trait-", "tier=Deep", "-trait-", "tier=Exhaustive"],
            Runs: 1
        ),
        // Two runs in two processes must write identical records, so a nondeterministic world fails the recording
        // rather than pinning one outcome of a coin flip.
        new(
            CheckedFiles: null,
            CommittedDirectory: "tests/Puck.World.Tests/ShippedWorldStateBaselines",
            Description: "Each shipped world's canonical state export and tick-cost record after its scripted sequence.",
            Name: "state",
            Project: "Puck.World.Tests",
            RunArguments: ["-class", "Puck.World.Tests.ShippedWorldStateBaselineTests"],
            Runs: 2
        ),
    ];

    public static Command Create() {
        var command = new Command(
            description: "Record a committed test baseline from a fresh run of its test, or check it.",
            name: Verb
        );

        foreach (var artifact in Artifacts) {
            command.Subcommands.Add(item: CreateArtifact(artifact: artifact));
        }

        command.Detail(detail: """
            Each subcommand builds its test project in Release, runs the tests that hold the
            baseline, and promotes the records they wrote beside their assembly
            (records/<artifact>) over the committed files. A test only compares; it never writes
            the checkout. --check writes nothing and exits 1 naming every committed file that
            differs from the fresh run. Exit 2 means the build failed or the run did not write
            every record.
            """);

        return command;
    }

    private static Command CreateArtifact(BaselineArtifact artifact) {
        var checkOption = new Option<bool>(name: "--check") { Description = "Compare the fresh run's records with the committed files and write nothing; exit 1 on any difference." };
        var command = new Command(
            description: artifact.Description,
            name: artifact.Name
        ) { checkOption };

        command.SetAction(action: parseResult => Run(
            artifact: artifact,
            check: parseResult.GetValue(option: checkOption)
        ));

        return command;
    }
    private static bool SameBytes(string left, string right) =>
        File.ReadAllBytes(path: left).AsSpan().SequenceEqual(other: File.ReadAllBytes(path: right));
    private static int Run(BaselineArtifact artifact, bool check) {
        var path = $"{Verb} {artifact.Name}";

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refuse(verb: path, what: "the repository root", why: "run inside the repository");
        }

        var project = Path.Combine(paths: [repositoryRoot, "tests", artifact.Project, $"{artifact.Project}.csproj"]);
        var output = Path.Combine(paths: [repositoryRoot, "tests", artifact.Project, "bin", "Release", "net10.0"]);
        var records = Path.Combine(paths: [output, "records", artifact.Name]);
        var committed = Path.Combine(
            path1: repositoryRoot,
            path2: artifact.CommittedDirectory
        );

        Console.Error.WriteLine(value: $"puck {path}: building {artifact.Project} (Release).");

        var build = CliProcess.RunCaptured(
            arguments: ["build", project, "-c", "Release", "--nologo", "-v", "q"],
            fileName: "dotnet",
            input: string.Empty,
            timeout: BuildBudget
        );

        if (build.TimedOut || (build.ExitCode != 0)) {
            Console.Error.Write(value: build.Stdout);
            Console.Error.Write(value: build.Stderr);

            return CliExit.Refuse(verb: path, what: artifact.Project, why: (build.TimedOut ? "the build timed out" : $"the build exited {build.ExitCode}"));
        }

        Dictionary<string, string>? first = null;

        for (var run = 1; (run <= artifact.Runs); run++) {
            if (Directory.Exists(path: records)) {
                Directory.Delete(
                    path: records,
                    recursive: true
                );
            }

            Console.Error.WriteLine(value: $"puck {path}: running {artifact.Project} ({run} of {artifact.Runs}).");

            var result = CliProcess.RunCaptured(
                arguments: [Path.Combine(path1: output, path2: $"{artifact.Project}.dll"), .. artifact.RunArguments],
                fileName: "dotnet",
                input: string.Empty,
                timeout: RunBudget,
                workingDirectory: repositoryRoot
            );

            // xUnit exits 1 when a test failed, which a stale baseline makes every compare do; anything else means
            // the run itself did not finish.
            if (result.TimedOut || (result.ExitCode is not (0 or 1))) {
                Console.Error.Write(value: result.Stdout);
                Console.Error.Write(value: result.Stderr);

                return CliExit.Refuse(verb: path, what: artifact.Project, why: (result.TimedOut ? "the test run timed out" : $"the test run exited {result.ExitCode}"));
            }
            if (!Directory.Exists(path: records) || !Directory.EnumerateFiles(path: records).Any()) {
                Console.Error.Write(value: result.Stdout);

                return CliExit.Refuse(verb: path, what: CliPaths.ToDisplay(fullPath: records), why: "the run wrote no records");
            }

            var written = Directory.EnumerateFiles(path: records).ToDictionary(
                elementSelector: static file => file,
                keySelector: static file => Path.GetFileName(path: file),
                comparer: StringComparer.Ordinal
            );

            if (first is null) {
                first = written.ToDictionary(
                    elementSelector: static entry => Path.Combine(
                        path1: Path.GetTempPath(),
                        path2: $"puck-baselines-{Environment.ProcessId}-{entry.Key}"
                    ),
                    keySelector: static entry => entry.Key,
                    comparer: StringComparer.Ordinal
                );

                foreach (var (name, file) in written) {
                    File.Copy(
                        destFileName: first[name],
                        overwrite: true,
                        sourceFileName: file
                    );
                }

                continue;
            }

            var differing = first.Keys.Union(second: written.Keys, comparer: StringComparer.Ordinal).Where(predicate: name =>
                (!first.ContainsKey(key: name) ||
                !written.ContainsKey(key: name) ||
                !SameBytes(left: first[name], right: written[name]))
            ).Order(comparer: StringComparer.Ordinal).ToArray();

            if (differing.Length != 0) {
                return CliExit.Refuse(verb: path, what: string.Join(separator: ", ", values: differing), why: "two runs wrote different records, so the baseline would pin a nondeterministic outcome");
            }
        }

        foreach (var copy in first!.Values) {
            File.Delete(path: copy);
        }

        var names = Directory.EnumerateFiles(path: records).Select(selector: static file => Path.GetFileName(path: file)!).Order(comparer: StringComparer.Ordinal).ToArray();

        return (check
            ? Check(
                artifact: artifact,
                committed: committed,
                names: names,
                path: path,
                records: records
            )
            : Promote(
                committed: committed,
                names: names,
                path: path,
                records: records
            )
        );
    }
    private static int Check(BaselineArtifact artifact, string committed, IReadOnlyList<string> names, string path, string records) {
        var drift = new List<string>();
        var compared = names.Where(predicate: name => (artifact.CheckedFiles?.Contains(value: name, comparer: StringComparer.Ordinal) ?? true)).ToArray();

        foreach (var name in compared) {
            var target = Path.Combine(
                path1: committed,
                path2: name
            );

            if (!File.Exists(path: target)) {
                drift.Add(item: $"{CliPaths.ToDisplay(fullPath: target)} is missing");
            } else if (!SameBytes(left: target, right: Path.Combine(path1: records, path2: name))) {
                drift.Add(item: $"{CliPaths.ToDisplay(fullPath: target)} differs from the fresh run");
            }
        }

        if (drift.Count == 0) {
            Console.Out.WriteLine(value: $"puck {path}: {compared.Length} record(s) match the committed files.");

            return CliExit.Success;
        }

        Console.Error.WriteLine(value: $"puck {path}: {drift.Count} record(s) drifted; re-record with 'puck {path}'.");

        foreach (var line in drift) {
            Console.Error.WriteLine(value: $"  {line}");
        }

        return CliExit.Failed;
    }
    private static int Promote(string committed, IReadOnlyList<string> names, string path, string records) {
        var written = 0;

        _ = Directory.CreateDirectory(path: committed);

        foreach (var name in names) {
            var source = Path.Combine(
                path1: records,
                path2: name
            );
            var target = Path.Combine(
                path1: committed,
                path2: name
            );

            if (File.Exists(path: target) && SameBytes(left: source, right: target)) {
                continue;
            }

            File.Copy(
                destFileName: target,
                overwrite: true,
                sourceFileName: source
            );
            Console.Out.WriteLine(value: $"puck {path}: wrote {CliPaths.ToDisplay(fullPath: target)}");
            written++;
        }

        Console.Out.WriteLine(value: $"puck {path}: {written} of {names.Count} record(s) changed.");

        return CliExit.Success;
    }
}
/// <summary>One committed baseline <c>puck baselines</c> records.</summary>
/// <param name="CheckedFiles">The file names <c>--check</c> compares, or <see langword="null"/> for every record.</param>
/// <param name="CommittedDirectory">The repository-relative directory holding the committed files.</param>
/// <param name="Description">The subcommand's help text.</param>
/// <param name="Name">The subcommand and the <c>records/&lt;name&gt;</c> directory the tests write.</param>
/// <param name="Project">The test project, named as its directory under <c>tests</c> and its assembly.</param>
/// <param name="RunArguments">The xUnit arguments selecting the tests that write the records.</param>
/// <param name="Runs">How many runs must write identical records before any is used.</param>
internal sealed record BaselineArtifact(
    IReadOnlyList<string>? CheckedFiles,
    string CommittedDirectory,
    string Description,
    string Name,
    string Project,
    IReadOnlyList<string> RunArguments,
    int Runs
);
