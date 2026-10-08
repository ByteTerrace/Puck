using System.CommandLine.Parsing;
using Puck.Cli.Affected;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Gate.Tests;

public sealed class GateBaselineLawTests : GateRunLaws {
    [InlineData("worlds/package/game.puck", "corpus-inventory")]
    [InlineData("VerifiedCode.json", "maths-ledger")]
    [InlineData("build/trigger.props", "browser-parity,corpus-inventory,maths-ledger,state")]
    [InlineData("docs/only.md", "")]
    [Theory]
    public void ReachedBaselinesRunOnceInOrderThroughAdmissionAndBothLogsBeforeGpu(string path, string names) {
        using var branches = new Branches();

        branches.Checkout.Write(name: path, text: "changed");
        branches.Checkout.Write(name: "tests/Puck.World.Canaries/example/positive.script.txt", text: "wire.errors\n\n");
        using var directory = new TemporaryDirectory(prefix: "puck-gate-baseline-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: ""));

        var gate = Gate(branches, runner, directory, gpu: true);

        Assert.Equal(actual: gate.ExitCode, expected: CliExit.Success);
        var expected = names.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ',');
        var checks = runner.Steps.Where(predicate: static arguments => (arguments[0] == "baselines")).ToArray();

        Assert.Equal(expected: expected, actual: checks.Select(selector: static arguments => arguments[1]));
        var log = File.ReadAllText(path: directory.PathOf(name: "gate.log"));
        var summary = File.ReadAllLines(path: directory.PathOf(name: "gate.steps"));
        var previous = runner.Events.IndexOf(item: "run derivations");

        foreach (var arguments in checks) {
            var name = ("baselines " + arguments[1]);
            var admit = runner.Events.IndexOf(item: ("admit " + name));

            Assert.True(condition: (admit > previous), userMessage: $"{name} needs admission after the preceding check.");
            Assert.Equal(expected: ("run " + name), actual: runner.Events[(admit + 1)]);
            Assert.Equal(expected: new[] { "baselines", arguments[1], "--check" }, actual: arguments);
            Assert.Contains(actualString: log, expectedSubstring: $"===== {name} (exit 0)");
            Assert.Single(collection: summary, predicate: line => line.Contains(value: $"start {name} exit=-"));
            Assert.Single(collection: summary, predicate: line => line.Contains(value: $"exit {name} exit=0"));
            previous = (admit + 1);
        }
        Assert.DoesNotContain(collection: runner.Steps[0], expected: "--gpu");
        Assert.True(condition: (runner.Events.IndexOf(item: "run affected canaries") > previous));
        Assert.True(condition: (runner.Events.IndexOf(item: "run parity") > runner.Events.IndexOf(item: "run affected canaries")));
        // Each canary's verdict reaches the console as it lands, and every step reports its wall time.
        Assert.Contains(actualString: gate.Output, expectedSubstring: "gate:   PASS: canary example held");
        Assert.Contains(actualString: gate.Output, expectedSubstring: "gate: affected canaries passed (");
        Assert.Contains(actualString: gate.Output, expectedSubstring: "gate: derivations passed (");
    }
    [Fact]
    public void PrintedBaselineCommandsParseAndAreExactlyWhatTheGateRuns() {
        using var branches = new Branches();

        branches.Checkout.Write(name: "build/trigger.props", text: "<Project />");
        Assert.True(condition: AffectedCommand.TryPlan(repositoryRoot: branches.Checkout.Root, schemaSourceTypes: SuiteRoot.Composition.SchemaSourceTypes, since: branches.Base,
            changed: out _, plan: out var plan, error: out var error), userMessage: error);
        using var text = new StringWriter();

        AffectedCommand.Describe(into: text, plan: plan!);
        var lines = text.ToString().Split(separator: Environment.NewLine, options: StringSplitOptions.RemoveEmptyEntries);
        var printed = new List<string[]>();

        foreach (var name in new[] { "browser-parity", "corpus-inventory", "maths-ledger", "state" }) {
            var index = Array.IndexOf(array: lines, value: ("baseline " + name));

            Assert.True(condition: (index >= 0), userMessage: $"Missing baseline {name} line.");
            Assert.StartsWith(expectedStartString: "puck ", actualString: lines[(index + 1)]);
            var arguments = CommandLineParser.SplitCommandLine(commandLine: lines[(index + 1)][5..]).ToArray();
            var parsed = SuiteRoot.Create().Parse(args: arguments);

            Assert.Empty(collection: parsed.Errors);
            Assert.Equal(actual: arguments, expected: new[] { "baselines", name, "--check" });
            printed.Add(item: arguments);
        }
        using var directory = new TemporaryDirectory(prefix: "puck-gate-baseline-argv-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: ""));

        Assert.Equal(expected: CliExit.Success, actual: Gate(branches, runner, directory).ExitCode);
        Assert.Equal(expected: printed, actual: runner.Steps.Where(predicate: static arguments => (arguments[0] == "baselines")));
    }
}
