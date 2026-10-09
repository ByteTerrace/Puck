using Puck.Cli.Automation;
using Xunit;

namespace Puck.Cli.Release.Tests.Automation;

/// <summary>
/// <c>puck artifacts test-windows</c> runs the archived assemblies on a runner with no GPU, over a restored archive
/// rather than a build. Each run takes the CPU selection the gate's suites take, so no device law runs on the runner's
/// software adapter, which never finishes the SDF interpreter kernels; and it leaves out the laws that read a build
/// tree, which a restored archive is not. Both are the shared spellings, never a second one.
/// </summary>
public sealed class ArtifactsTestSelectionLawTests {
    [Fact]
    public void ATestWindowsRunTakesTheCpuSelectionAndLeavesOutTheBuildTreeLaws() {
        var arguments = ArtifactsCommand.TestWindowsArguments(assembly: "tests/A/bin/Release/net10.0/A.dll", report: "000-A.trx", results: "artifacts/test-results");

        Assert.Equal(expected: "tests/A/bin/Release/net10.0/A.dll", actual: arguments[0]);
        Assert.Equal(expected: ["--filter-not-trait", "Category=Gpu"], actual: CliTestRun.CpuSelection);
        Assert.Equal(expected: ["--filter-not-trait", "Category=BuildTree"], actual: CliTestRun.WithoutBuildTree);
        foreach (var selection in ((string[][])[CliTestRun.CpuSelection, CliTestRun.WithoutBuildTree])) {
            Assert.Contains(
                collection: Enumerable.Range(start: 1, count: (arguments.Length - 1)),
                filter: index => ((arguments[index - 1] == selection[0]) && (arguments[index] == selection[1]))
            );
        }
        // A trait the run includes would narrow it to that trait alone; the run excludes and never includes.
        Assert.DoesNotContain(expected: "--filter-trait", collection: arguments);
    }
}
