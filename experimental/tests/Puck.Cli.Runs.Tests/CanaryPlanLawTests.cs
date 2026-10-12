using Puck.Cli.Canary;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// Proves what <c>puck canary --plan</c> counts: the World boots, runner-started processes, builds and summed leg
/// budget of a selection, from its manifests alone; which legs run alone; and that each gate selection is refused
/// once it outgrows the cost recorded in <see cref="CanaryCeilingsLedger"/>.
/// </summary>
public sealed class CanaryPlanLawTests {
    private static CanaryManifest[] Shipped() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.True(condition: CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: repositoryRoot,
            strict: true
        ), userMessage: error);

        return [.. manifests];
    }

    /// <summary>A run whose warm boot cannot start a World fails the selection with exit 2, naming the backend, and
    /// starts no leg.</summary>
    [Fact]
    public void AFailedWarmFailsTheSelectionByNameBeforeAnyLeg() {
        using var scratch = new TemporaryDirectory(prefix: "puck-warm-refusal-");
        var root = scratch.RootPath;

        var artifact = Path.Combine(path1: root, path2: "Puck.World.dll");

        File.WriteAllBytes(bytes: [], path: artifact);

        var (exitCode, output, error) = ConsoleCapture.RunSplit(run: () => SuiteRoot.Invoke(args: ["canary", "sdf-mesh-motion", "--backend", "vulkan", "--world-artifact", artifact]));
        // Every assertion names the whole run, so a failure shows what the verb wrote instead of what it did not.
        var transcript = $"exit {exitCode}{Environment.NewLine}--- stdout ---{Environment.NewLine}{output}--- stderr ---{Environment.NewLine}{error}";

        Assert.True(condition: (exitCode == CliExit.Refused), userMessage: transcript);
        Assert.True(condition: error.Contains(comparisonType: StringComparison.Ordinal, value: "ERROR: the pipeline-cache warm on vulkan from sdf-mesh-motion's positive world exited"), userMessage: transcript);
        Assert.True(condition: error.Contains(comparisonType: StringComparison.Ordinal, value: "The selection fails without starting a leg."), userMessage: transcript);
        Assert.False(condition: output.Contains(comparisonType: StringComparison.Ordinal, value: "sdf-mesh-motion on vulkan positive"), userMessage: transcript);
    }
    [Fact]
    public void TheShippedGateSelectionsFitTheirRecordedCeilings() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.True(
            condition: CanaryCeilingsLedger.TryRead(
                error: out var error,
                ledger: out var ledger,
                repositoryRoot: repositoryRoot,
                text: out _
            ),
            userMessage: error
        );

        var manifests = Shipped();
        var automatic = CanaryCommand.Plan(
            manifests: [.. manifests.Where(predicate: static manifest => manifest.IsAutomatic)],
            namedWorldArtifact: false,
            backends: WorldOffscreenLeg.Backends
        );
        var merge = CanaryCommand.Plan(
            manifests: CanaryCommand.SelectMerge(manifests: manifests),
            namedWorldArtifact: false,
            backends: WorldOffscreenLeg.Backends
        );

        Assert.Null(@object: CanaryCommand.CeilingRefusal(
            ceiling: ledger!.Automatic,
            name: "automatic",
            plan: automatic
        ));
        Assert.Null(@object: CanaryCommand.CeilingRefusal(
            ceiling: ledger.Merge,
            name: "merge",
            plan: merge
        ));
    }
    [Fact]
    public void PlanningTheMergeGatePrintsTheSameCountsEveryTimeWithoutRunningAnything() {
        var first = ConsoleCapture.RunSplit(run: static () => SuiteRoot.Invoke(args: ["canary", "--merge", "--plan"]));
        var second = ConsoleCapture.RunSplit(run: static () => SuiteRoot.Invoke(args: ["canary", "--merge", "--plan"]));
        var merge = CanaryCommand.Plan(
            manifests: CanaryCommand.SelectMerge(manifests: Shipped()),
            namedWorldArtifact: false,
            backends: WorldOffscreenLeg.Backends
        );

        Assert.Equal(
            actual: first.ExitCode,
            expected: 0
        );
        Assert.Equal(
            actual: second.Output,
            expected: first.Output
        );
        Assert.Contains(expectedSubstring: $"canary plan: {merge.WorldBoots} World boot(s); {merge.LegSpawns} leg process spawn(s)", actualString: first.Output);
        Assert.DoesNotContain(actualString: first.Error, expectedSubstring: "building Puck.World");
        Assert.DoesNotContain(actualString: first.Error, expectedSubstring: "reusing the Puck.World build");
    }
}
