using System.CommandLine;

using Puck.Cli.Gate;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// The gate runs every drift check a verb offers: each command carrying a <c>--check</c> option is a step of
/// <see cref="GatePlan.Steps"/> or carries a reason in <see cref="GatePlan.CheckExclusions"/>, so a generated file
/// whose check no step runs cannot drift unnoticed.
/// </summary>
public sealed class GatePlanCoversCheckVerbsLawTests {
    // The verb path a step runs: its arguments up to the first option.
    private static string PathOf(IEnumerable<string> arguments) => string.Join(
        separator: ' ',
        values: arguments.TakeWhile(predicate: static argument => !argument.StartsWith(comparisonType: StringComparison.Ordinal, value: "-"))
    );
    private static void Walk(Command command, List<string> path, List<string> found) {
        if (
            (path.Count > 0) &&
            command.Options.Any(predicate: static option => (option.Name == "--check"))
        ) {
            found.Add(item: string.Join(separator: ' ', values: path));
        }

        foreach (var subcommand in command.Subcommands) {
            path.Add(item: subcommand.Name);
            Walk(command: subcommand, found: found, path: path);
            path.RemoveAt(index: (path.Count - 1));
        }
    }

    [Fact]
    public void EveryCheckVerbHasAGateStepOrAReasonedExclusion() {
        var found = new List<string>();

        Walk(command: PuckRootCommand.Create(clock: TimeProvider.System), found: found, path: []);

        var stepped = GatePlan.Steps.Select(selector: static step => PathOf(arguments: step.Arguments)).ToHashSet(comparer: StringComparer.Ordinal);
        var uncovered = found.Where(predicate: path => !stepped.Contains(item: path) && !GatePlan.CheckExclusions.ContainsKey(key: path)).Order(comparer: StringComparer.Ordinal).ToList();
        var stale = GatePlan.CheckExclusions.Keys.Where(predicate: key => !found.Contains(item: key) || stepped.Contains(item: key)).Order(comparer: StringComparer.Ordinal).ToList();

        Assert.True(
            condition: (uncovered.Count == 0),
            userMessage: $"These verbs offer --check and run in no gate step; add a step or a reasoned exclusion to GatePlan:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: uncovered)}"
        );
        Assert.True(
            condition: (stale.Count == 0),
            userMessage: $"These exclusions name no --check verb or a verb a step runs; delete them from GatePlan.CheckExclusions:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: stale)}"
        );
    }
}
