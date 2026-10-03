using System.Text;
using System.Text.Json;
using Puck.Cli.Affected;

namespace Puck.Cli.Gate;

/// <summary>One step's outcome: its exit code and everything it wrote, both streams together.</summary>
internal sealed record GateStepResult(int ExitCode, string Output);
/// <summary>Runs the gate's steps. <see cref="ProcessGateRunner"/> is the real one; the laws over <see cref="GateRun"/>
/// substitute their own, so no law builds the solution or touches a GPU.</summary>
internal interface IGateRunner {
    GateStepResult Dotnet(string repositoryRoot, IReadOnlyList<string> arguments);
    string CopyCli(string repositoryRoot, string directory);
    /// <summary>Runs one verb of the copied CLI against the checkout; <paramref name="progress"/>, when given, sees each
    /// line the verb writes as it writes it, so a long step can report while it runs.</summary>
    GateStepResult Puck(string cli, string repositoryRoot, IReadOnlyList<string> arguments, Action<string>? progress = null);
    bool WaitForCapacity(string repositoryRoot, string step);
}
/// <summary>Executes the batch plan serially, recording each step and withholding coverage on any failure.</summary>
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
    /// <summary>Runs the selected plan using the CLI host's clock and a substitutable process/admission boundary.</summary>
    public static int Run(string repositoryRoot, string target, bool gpu, bool record, IGateRunner runner, string directory, TimeProvider clock) {
        if (record && !gpu) {
            return CliExit.Refuse(verb: Verb, what: "--record", why: "requires --gpu and an all-green qualification.");
        }
        if (!AffectedCommand.TryResolveBase(error: out var baseError, mergeBase: target, repositoryRoot: repositoryRoot, resolved: out var mergeBase, since: null)) {
            return CliExit.Refuse(verb: Verb, what: target, why: baseError);
        }
        if (!AffectedCommand.TryReadChanged(changed: out var changed, deleted: out var deleted, error: out var changeError, repositoryRoot: repositoryRoot, since: mergeBase)) {
            return CliExit.Refuse(verb: Verb, what: mergeBase, why: changeError);
        }
        if (!AffectedCommand.TryPlan(changed: out _, error: out var planError, plan: out var affected, repositoryRoot: repositoryRoot, since: mergeBase)) {
            return CliExit.Refuse(verb: Verb, what: mergeBase, why: planError);
        }
        var logPath = Path.Combine(path1: directory, path2: "gate.log");
        var stepsPath = Path.Combine(path1: directory, path2: "gate.steps");
        var shownLog = CliPaths.ToDisplay(fullPath: logPath);
        var shownSteps = CliPaths.ToDisplay(fullPath: stepsPath);
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var summary = new StreamWriter(stepsPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        var cliDirectory = Path.Combine(path1: directory, path2: "cli");
        var fileList = Path.Combine(path1: directory, path2: "format-sources.json");
        var sources = FormatSources(changed: changed, deleted: deleted, repositoryRoot: repositoryRoot);

        using (var stream = File.Create(path: fileList)) {
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartArray();
            foreach (var source in sources) { writer.WriteStringValue(value: source); }
            writer.WriteEndArray();
        }
        var failed = new List<string>();
        var refused = false;
        var cli = string.Empty;

        Console.Out.WriteLine(value: $"gate: {changed.Count} changed file(s) against {mergeBase[..12]}, the merge base of HEAD and {target}; full output in {shownLog}; steps in {shownSteps}.");
        try {
            foreach (var step in GatePlan.Expand(repositoryRoot, mergeBase, fileList, (sources.Count > 0), gpu, record, affected!)) {
                if (step.Record && (failed.Count > 0)) {
                    Console.Out.WriteLine(value: $"gate: {step.Name} skipped; qualification failed.");
                    continue;
                }
                if (step.Heavy && !runner.WaitForCapacity(repositoryRoot: repositoryRoot, step: step.Name)) {
                    refused = true;
                    Console.Error.WriteLine(value: $"gate: {step.Name} refused; host capacity did not return.");
                    break;
                }
                Console.Error.WriteLine(value: $"gate: running {step.Name}.");
                var started = clock.GetTimestamp();

                summary.WriteLine(value: $"{clock.GetUtcNow():O} start {step.Name} exit=- elapsed=0s");
                GateStepResult result;

                try {
                    switch (step.Kind) {
                        case GateStepKind.Build:
                        case GateStepKind.DeviceSuite:
                            result = runner.Dotnet(repositoryRoot, step.Arguments);
                            break;
                        case GateStepKind.CopyCli:
                            Directory.CreateDirectory(path: cliDirectory);
                            cli = runner.CopyCli(directory: cliDirectory, repositoryRoot: repositoryRoot);
                            result = new GateStepResult(ExitCode: 0, Output: CliPaths.ToDisplay(fullPath: cli));
                            break;
                        case GateStepKind.Canaries:
                            // Each canary's verdict is echoed as it lands, so a long GPU leg shows its progress.
                            result = runner.Puck(cli, repositoryRoot, step.Arguments, progress: static line => {
                                if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "PASS: canary ") || line.StartsWith(comparisonType: StringComparison.Ordinal, value: "FAIL: canary ")) {
                                    Console.Out.WriteLine(value: $"gate:   {line}");
                                }
                            });
                            break;
                        default:
                            result = runner.Puck(cli, repositoryRoot, step.Arguments);
                            break;
                    }
                } catch (Exception exception) {
                    result = new GateStepResult(ExitCode: CliExit.Refused, Output: exception.ToString());
                }
                var elapsed = ((long)clock.GetElapsedTime(startingTimestamp: started).TotalSeconds);

                summary.WriteLine(value: $"{clock.GetUtcNow():O} exit {step.Name} exit={result.ExitCode} elapsed={elapsed}s");
                Log(log: log, result: result, step: step.Name);
                if (result.ExitCode == 0) {
                    Console.Out.WriteLine(value: $"gate: {step.Name} passed ({elapsed}s)");
                    continue;
                }
                failed.Add(item: step.Name);
                var prerequisite = (step.Kind is GateStepKind.Build or GateStepKind.CopyCli);

                Console.Out.WriteLine(value: $"gate: {step.Name} FAILED (exit {result.ExitCode}, {elapsed}s){(prerequisite ? "; nothing else ran." : string.Empty)}");
                var lines = Lines(text: result.Output);
                var errors = lines.Where(predicate: line => line.Contains(comparisonType: StringComparison.Ordinal, value: ": error ")).Distinct(comparer: StringComparer.Ordinal).ToArray();

                foreach (var line in ((prerequisite && (errors.Length > 0)) ? errors : lines.TakeLast(count: FailureTail))) {
                    Console.Out.WriteLine(value: $"  {line.Trim()}");
                }
                if (prerequisite) { break; }
            }
        } finally {
            _ = RunDirectory.TryDelete(path: cliDirectory);
            File.Delete(path: fileList);
        }
        var verdict = (refused ? "refused" : ((failed.Count == 0) ? "passed" : $"FAILED: {string.Join(separator: ", ", values: failed)}"));

        Console.Out.WriteLine(value: $"gate: {verdict}; full output in {shownLog}; steps in {shownSteps}");
        return (refused ? CliExit.Refused : ((failed.Count == 0) ? CliExit.Success : CliExit.Failed));
    }
}
