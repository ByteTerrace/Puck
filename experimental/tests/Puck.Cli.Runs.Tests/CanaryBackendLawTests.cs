using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// Proves <c>puck canary --backend</c> without a GPU: it selects both backends when omitted and only the named one
/// otherwise, it narrows only the proofs that declare backends, it refuses an unknown backend by name and the merge
/// gate outright, and the plan names the backends that run, so a run on one backend never reads as a run on both.
/// </summary>
public sealed class CanaryBackendLawTests {
    private static (int ExitCode, string Output, string Error) Canary(params string[] arguments) =>
        ConsoleCapture.RunSplit(run: () => SuiteRoot.Invoke(args: ["canary", .. arguments]));

    // The shipped offscreen proofs, planned through the real command tree: the default plans both backends and says
    // so; --backend plans only the named one and says which did not run. Planning builds and boots nothing.
    [Fact]
    public void PlanningThroughTheCommandNamesTheBackendsThatRun() {
        var both = Canary("--capability", "offscreen", "--plan");
        var vulkan = Canary("--capability", "offscreen", "--plan", "--backend", "vulkan");

        Assert.Equal(
            actual: both.ExitCode,
            expected: 0
        );
        Assert.Contains(actualString: both.Output, expectedSubstring: "canary plan pipeline-feedback on vulkan:");
        Assert.Contains(actualString: both.Output, expectedSubstring: "canary plan pipeline-feedback on directx:");
        Assert.Contains(actualString: both.Output, expectedSubstring: "canary plan: backend-declaring proofs run on vulkan and directx.");
        Assert.Equal(
            actual: vulkan.ExitCode,
            expected: 0
        );
        Assert.Contains(actualString: vulkan.Output, expectedSubstring: "canary plan pipeline-feedback on vulkan:");
        Assert.DoesNotContain(actualString: vulkan.Output, expectedSubstring: " on directx:");
        Assert.Contains(actualString: vulkan.Output, expectedSubstring: "canary plan: backend-declaring proofs run on vulkan only (--backend vulkan), not on directx.");
        Assert.DoesNotContain(actualString: vulkan.Error, expectedSubstring: "building Puck.World");
    }
}
