using System.CommandLine;
using Puck.Cli.Affected;
using Puck.Cli.Baselines;
using Puck.Cli.Gate;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck gate</c>'s plan against the composed tool: every step it expands parses through the
/// root command, every <c>--check</c> in the tree has a step or a reasoned exclusion, and the help and the CLI reference
/// list the whole plan in order.</summary>
public sealed class GatePlanLawTests {
    private static IEnumerable<(string Path, Command Command)> Commands(Command parent, string prefix = "") {
        foreach (var command in parent.Subcommands) {
            var path = (prefix + command.Name);

            yield return (path, command);
            foreach (var nested in Commands(parent: command, prefix: (path + " "))) { yield return nested; }
        }
    }

    [Fact]
    public void EveryPuckStepThePlanExpandsParsesThroughTheRootCommand() {
        using var directory = new TemporaryDirectory(prefix: "puck-gate-plan-grammar-law-");

        var affected = new AffectedPlan(Baselines: BaselinesCommand.Artifacts, Canaries: ["example"], CanaryChecks: [], Catalog: false, Deleted: [], Everything: true, Parity: true, Suites: [], Unmapped: [], Worlds: []);
        var steps = GatePlan.Expand(affected: affected, fileList: "files.json", gpu: true, mergeBase: "HEAD", record: true, repositoryRoot: directory.RootPath, sources: true, suiteJobs: 4, gpuJobs: 4)
            .Where(predicate: static step => (step.Kind is GateStepKind.Locks or GateStepKind.Puck or GateStepKind.Baseline or GateStepKind.Canaries or GateStepKind.Parity))
            .ToArray();

        foreach (var step in steps) {
            var parsed = PuckRootCommand.Create(clock: TimeProvider.System).Parse(args: step.Arguments);

            Assert.True(condition: (parsed.Errors.Count == 0),
                userMessage: $"puck {string.Join(separator: ' ', value: step.Arguments)}: {string.Join(separator: "; ", values: parsed.Errors.Select(selector: static error => error.Message))}");
        }
    }
    [Fact]
    public void EveryCheckInTheEntireRootTreeHasAGateStepOrAReasonedExclusion() {
        var checks = Commands(PuckRootCommand.Create(clock: TimeProvider.System)).Where(predicate: entry => entry.Command.Options.Any(predicate: option => ((option.Name == "--check") || option.Aliases.Contains(item: "--check")))).Select(selector: entry => entry.Path).ToHashSet(comparer: StringComparer.Ordinal);
        var included = GatePlan.Steps.Where(predicate: step => step.Arguments.Contains(value: "--check")).Select(selector: step => string.Join(separator: ' ', values: step.Arguments.TakeWhile(predicate: argument => !argument.StartsWith(comparisonType: StringComparison.Ordinal, value: "--")))).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var check in checks) { Assert.True(condition: (included.Contains(item: check) || GatePlan.CheckExclusions.ContainsKey(key: check)), userMessage: $"puck {check} --check needs a gate step or an explicit reasoned exclusion in GatePlan."); }
        foreach (var (excluded, reason) in GatePlan.CheckExclusions) {
            Assert.Contains(expected: excluded, set: checks);
            Assert.DoesNotContain(expected: excluded, set: included);
            Assert.False(condition: string.IsNullOrWhiteSpace(value: reason));
        }
        Assert.All(included, check => Assert.Contains(expected: check, set: checks));
        foreach (var artifact in BaselinesCommand.Artifacts) {
            Assert.False(condition: string.IsNullOrWhiteSpace(value: artifact.Project));
            Assert.All(collection: artifact.Inputs, action: static input => Assert.False(condition: string.IsNullOrWhiteSpace(value: input)));
            Assert.Contains(collection: GatePlan.Steps, filter: step => ((step.Kind == GateStepKind.Baseline) && step.Arguments.SequenceEqual(other: artifact.CheckArguments())));
        }
    }
    [Fact]
    public void TheRenderedHelpAndCliReferenceContainTheWholePlanInOrder() {
        var command = GateCommand.Create(clock: TimeProvider.System, composition: PuckRootCommand.Gate);

        Assert.Contains(GatePlan.Detail(), CliHelp.DetailOf(command: command));
        var help = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["gate", "--help"]));

        Assert.Equal(actual: help.ExitCode, expected: 0);
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));
        var reference = File.ReadAllText(path: Path.Combine(path1: root, path2: "docs/reference/cli.md"));
        var begin = reference.IndexOf(comparisonType: StringComparison.Ordinal, value: "## `puck gate`");
        var end = reference.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: (begin + 1), value: "\n## ");
        var section = reference[begin..end];
        var helpPosition = 0;
        var docPosition = 0;

        foreach (var step in GatePlan.Steps) {
            helpPosition = help.Output.IndexOf((step.Name + ":"), helpPosition, StringComparison.Ordinal);
            docPosition = section.IndexOf((("`" + step.Name) + "`"), docPosition, StringComparison.Ordinal);
            Assert.True(condition: (helpPosition >= 0), userMessage: $"help lacks ordered step {step.Name}");
            Assert.True(condition: (docPosition >= 0), userMessage: $"cli.md gate section lacks ordered step {step.Name}");
            helpPosition += step.Name.Length;
            docPosition += (step.Name.Length + 2);
        }
    }
}
