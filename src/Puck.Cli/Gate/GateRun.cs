using System.Text;
using System.Text.Json;
using Puck.Cli.Affected;

namespace Puck.Cli.Gate;

/// <summary>One step's outcome: its exit code and everything it wrote, both streams together.</summary>
internal sealed record GateStepResult(int ExitCode, string Output);
/// <summary>Runs the gate's steps. <see cref="ProcessGateRunner"/> is the real one; the laws over <see cref="GateRun"/>
/// substitute their own, so no law builds the solution or touches a GPU.</summary>
internal interface IGateRunner {
    /// <summary>Builds the solution in Release.</summary>
    /// <param name="repositoryRoot">The checkout to build.</param>
    /// <returns>The build's outcome.</returns>
    GateStepResult Build(string repositoryRoot);
    /// <summary>Copies the CLI the build just wrote into <paramref name="directory"/>.</summary>
    /// <param name="repositoryRoot">The checkout whose build output holds the CLI.</param>
    /// <param name="directory">The empty directory that receives the copy.</param>
    /// <returns>The copied <c>Puck.Cli.dll</c>'s full path.</returns>
    string CopyCli(string repositoryRoot, string directory);
    /// <summary>Runs one verb of the copied CLI against the checkout.</summary>
    /// <param name="cli">The copied <c>Puck.Cli.dll</c>.</param>
    /// <param name="repositoryRoot">The checkout, the verb's working directory.</param>
    /// <param name="arguments">The verb and its arguments.</param>
    /// <returns>The verb's outcome.</returns>
    GateStepResult Puck(string cli, string repositoryRoot, IReadOnlyList<string> arguments);
}
/// <summary>
/// <c>puck gate</c>'s run: the change-scoped CPU gate for a branch. It builds the solution and stops there, showing the
/// build's errors, when the build fails; copies the CLI that build wrote into the run's own directory, so every later
/// step runs the candidate's code and no other run can overwrite it; resolves the merge base of <c>HEAD</c> and the
/// target; runs <c>puck affected --merge-base &lt;base&gt; --run</c> on that copy, adding <c>--gpu</c> when asked; and
/// runs the repository checks in their check forms only. Every step's full output goes to the log the run names; the
/// console carries one verdict line a step.
/// </summary>
internal static class GateRun {
    /// <summary>The branch a change is gated against unless <c>--merge-base</c> names another.</summary>
    public const string DefaultTarget = "origin/features/gfx-pipeline";

    private const int FailureTail = 15;
    private const string Verb = "gate";

    private static IReadOnlyList<string> Lines(string text) => [.. text.Split(separator: '\n')
        .Select(selector: static line => line.TrimEnd(trimChar: '\r'))
        .Where(predicate: static line => (line.Length > 0))];
    private static void Log(StreamWriter log, string step, GateStepResult result) {
        log.WriteLine(value: $"===== {step} (exit {result.ExitCode})");
        log.WriteLine(value: result.Output.TrimEnd());
        log.WriteLine();
        log.Flush();
    }

    /// <summary>Returns the changed sources <c>puck format --check</c> is limited to: every C# and <c>.puck</c> file
    /// changed since the base and still present, outside <c>experimental/</c> and generated C#.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="changed">Every changed path.</param>
    /// <param name="deleted">The changed paths deleted since the base.</param>
    /// <returns>The sources, repository-relative, in ordinal order.</returns>
    public static IReadOnlyList<string> FormatSources(string repositoryRoot, IReadOnlyList<string> changed, IReadOnlySet<string> deleted) => [.. changed
        .Where(predicate: path => !deleted.Contains(item: path))
        .Where(predicate: static path => (path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cs") || path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".puck")))
        .Where(predicate: static path => !path.StartsWith(comparisonType: StringComparison.Ordinal, value: "experimental/"))
        .Where(predicate: static path => !(path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".g.cs") || path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".generated.cs")))
        .Where(predicate: path => File.Exists(path: Path.Combine(path1: repositoryRoot, path2: path)))
        .Order(comparer: StringComparer.Ordinal)];
    /// <summary>Runs the gate.</summary>
    /// <param name="repositoryRoot">The checkout to gate.</param>
    /// <param name="target">The branch the change lands on; the change is read against its merge base with
    /// <c>HEAD</c>.</param>
    /// <param name="gpu">Whether to run the chosen canaries and parity after the CPU checks.</param>
    /// <param name="runner">Runs the steps.</param>
    /// <param name="directory">The run's own empty directory: it receives the CLI copy, the format file list and the log,
    /// and keeps the log.</param>
    /// <returns><see cref="CliExit.Success"/> when every step passed, <see cref="CliExit.Failed"/> when the build or any
    /// step failed, and <see cref="CliExit.Refused"/> when no merge base could be resolved.</returns>
    public static int Run(string repositoryRoot, string target, bool gpu, IGateRunner runner, string directory) {
        if (!AffectedCommand.TryResolveBase(error: out var baseError, mergeBase: target, repositoryRoot: repositoryRoot, resolved: out var mergeBase, since: null)) {
            return CliExit.Refuse(verb: Verb, what: target, why: baseError);
        }

        if (!AffectedCommand.TryReadChanged(changed: out var changed, deleted: out var deleted, error: out var changeError, repositoryRoot: repositoryRoot, since: mergeBase)) {
            return CliExit.Refuse(verb: Verb, what: mergeBase, why: changeError);
        }

        var logPath = Path.Combine(
            path1: directory,
            path2: "gate.log"
        );
        var shownLog = CliPaths.ToDisplay(fullPath: logPath);
        using var log = new StreamWriter(
            append: false,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            path: logPath
        );

        Console.Out.WriteLine(value: $"gate: {changed.Count} changed file(s) against {mergeBase[..12]}, the merge base of HEAD and {target}; full output in {shownLog}.");
        Console.Error.WriteLine(value: "gate: building the solution.");

        var build = runner.Build(repositoryRoot: repositoryRoot);

        Log(log: log, result: build, step: "build");

        if (build.ExitCode != 0) {
            var output = Lines(text: build.Output);
            var errors = output.Where(predicate: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: ": error ")).Distinct(comparer: StringComparer.Ordinal).ToArray();

            Console.Out.WriteLine(value: $"gate: build FAILED (exit {build.ExitCode}); nothing else ran.");

            foreach (var line in ((errors.Length > 0) ? errors : output.TakeLast(count: FailureTail))) {
                Console.Out.WriteLine(value: $"  {line.Trim()}");
            }

            return CliExit.Failed;
        }

        Console.Out.WriteLine(value: "gate: build passed");

        var cliDirectory = Path.Combine(
            path1: directory,
            path2: "cli"
        );

        _ = Directory.CreateDirectory(path: cliDirectory);

        var cli = runner.CopyCli(directory: cliDirectory, repositoryRoot: repositoryRoot);
        var sources = FormatSources(changed: changed, deleted: deleted, repositoryRoot: repositoryRoot);
        var fileList = Path.Combine(
            path1: directory,
            path2: "format-sources.json"
        );

        using (var stream = File.Create(path: fileList)) {
            using var writer = new Utf8JsonWriter(utf8Json: stream);

            writer.WriteStartArray();

            foreach (var source in sources) {
                writer.WriteStringValue(value: source);
            }

            writer.WriteEndArray();
        }

        List<(string Name, string[] Arguments)> steps = [
            ("affected", ["affected", "--merge-base", mergeBase, "--run", .. (gpu ? (string[])["--gpu"] : [])]),
        ];

        if (sources.Count > 0) {
            steps.Add(item: ("format", ["format", "--check", "--file-list", fileList]));
        }

        steps.Add(item: ("lengths", ["lengths", "--check"]));
        steps.Add(item: ("comment-smells", ["comment-smells", "--check"]));
        steps.Add(item: ("docs links", ["docs", "links"]));
        steps.Add(item: ("schema", ["schema", "--check"]));
        steps.Add(item: ("architecture", ["architecture", "--check"]));
        steps.Add(item: ("registry", ["registry", "--check"]));
        steps.Add(item: ("vocabulary", ["vocabulary", "--check"]));
        steps.Add(item: ("shaders generate", ["shaders", "generate", "--check"]));
        steps.Add(item: ("branding", ["branding", "--check"]));
        steps.Add(item: ("formats", ["formats", "--check"]));
        steps.Add(item: ("canary-ceilings", ["canary-ceilings", "--check"]));

        var failed = new List<string>();

        try {
            if (sources.Count == 0) {
                Console.Out.WriteLine(value: "gate: format skipped; no changed C# or .puck source");
            }

            foreach (var (name, arguments) in steps) {
                Console.Error.WriteLine(value: $"gate: running puck {string.Join(separator: ' ', values: arguments)}.");

                var result = runner.Puck(arguments: arguments, cli: cli, repositoryRoot: repositoryRoot);

                Log(log: log, result: result, step: name);

                if (result.ExitCode == 0) {
                    Console.Out.WriteLine(value: $"gate: {name} passed");

                    continue;
                }

                failed.Add(item: name);
                Console.Out.WriteLine(value: $"gate: {name} FAILED (exit {result.ExitCode})");

                foreach (var line in Lines(text: result.Output).TakeLast(count: FailureTail)) {
                    Console.Out.WriteLine(value: $"  {line.Trim()}");
                }
            }
        } finally {
            _ = RunDirectory.TryDelete(path: cliDirectory);
            File.Delete(path: fileList);
        }

        Console.Out.WriteLine(value: ((failed.Count == 0)
            ? $"gate: passed; full output in {shownLog}"
            : $"gate: FAILED: {string.Join(separator: ", ", values: failed)}; full output in {shownLog}"));

        return ((failed.Count == 0)
            ? CliExit.Success
            : CliExit.Failed);
    }
}
