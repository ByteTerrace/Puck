using Puck.Cli.Affected;
using Puck.Cli.Gate;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class GateNonExecutingEditsLawTests : GateRunLaws {
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void NonExecutingEditsKeepTheRepositoryChecks(bool prose, bool gpu) {
        using var branches = new Branches();
        const string Source = "src/Puck.World/Shared.cs";
        const string Manifest = "tests/Puck.World.Canaries/example/canary.json";

        branches.Checkout.Write(name: Source, text: "class Shared {}\n");
        branches.Checkout.Write(name: "src/Puck.World/Puck.World.csproj", text: "<Project />");
        branches.Checkout.Write(name: "tests/Puck.World.Tests/Puck.World.Tests.csproj", text: "<Project><ItemGroup><ProjectReference Include=\"../../src/Puck.World/Puck.World.csproj\" /></ItemGroup></Project>");
        var baseline = branches.Checkout.Commit(message: "source syntax");
        var path = (prose ? Manifest : Source);

        branches.Checkout.Write(name: path, text: (prose
            ? branches.Checkout.Read(name: path).Replace(comparisonType: StringComparison.Ordinal, newValue: "\"binding\": \"clearer binding\"", oldValue: "\"binding\": \"gate selection\"")
            : "// clearer comment\nclass Shared {}\n"));

        Assert.True(condition: AffectedCommand.TryPlan(repositoryRoot: branches.Checkout.Root, since: baseline, plan: out var plan, changed: out var changed, error: out var error), userMessage: error);
        Assert.Equal(actual: changed, expected: [path]);
        Assert.Empty(collection: plan!.Canaries);
        Assert.Empty(collection: plan.Suites);
        Assert.Empty(collection: plan.Unmapped);
        Assert.Equal(expected: (prose ? ["example"] : Array.Empty<string>()), actual: plan.CanaryChecks);

        using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty));

        var (exitCode, _, _) = Gate(branches: branches, directory: directory, runner: runner, gpu: gpu, target: baseline);

        Assert.Equal(actual: exitCode, expected: CliExit.Success);
        Assert.Contains(collection: runner.Steps, filter: step => step.SequenceEqual(other: ["affected", "--merge-base", baseline, "--run", "--suite-jobs", "2"]));
        Assert.Contains(collection: runner.Steps, filter: step => step.SequenceEqual(other: ["lengths", "--check"]));
        Assert.Contains(collection: runner.Steps, filter: step => step.SequenceEqual(other: ["comment-smells", "--check"]));
        Assert.Contains(collection: runner.Steps, filter: step => step.SequenceEqual(other: ["docs", "links"]));
        Assert.Equal(expected: gpu, actual: runner.Steps.Any(predicate: step => step.SequenceEqual(other: ["docs", "citations"])));
        Assert.Equal(expected: !prose, actual: runner.Steps.Any(predicate: step => ((step[0] == "format") && (step[1] == "--check"))));
        if (!prose) { Assert.Equal(expected: [Source], actual: runner.FormatSources); }
        Assert.DoesNotContain(collection: runner.Steps, filter: step => (step[0] is "canary" or "parity"));
    }
}
