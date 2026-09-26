using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves <c>puck canary --backend</c> without a GPU: it selects both backends when omitted and only the named one
/// otherwise, it narrows only the proofs that declare backends, it refuses an unknown backend by name and the merge
/// gate outright, and the plan names the backends that run, so a run on one backend never reads as a run on both.
/// </summary>
public sealed class CanaryBackendLawTests {
    private static CanaryLeg Leg(string name) => new(
        Assertions: [],
        Authorities: [],
        AuthorityWorldPath: null,
        Commands: [],
        Connect: false,
        Name: name,
        ScriptPath: $"{name}.script.txt",
        WorldPath: "world.json"
    );
    private static CanaryManifest Manifest(string id, CanaryBootShape shape, params string[] backends) => new(
        Backends: backends,
        Binding: id,
        BootShape: shape,
        DirectoryPath: id,
        Discriminating: Leg(name: "discriminating"),
        Fixtures: [],
        Id: id,
        Positive: Leg(name: "positive"),
        Requirements: ((backends.Length == 0)
            ? []
            : ["gpu"]),
        TimeoutSeconds: 10,
        Title: id
    );
    private static CanaryManifest[] Mixed() => [
        Manifest(backends: ["vulkan", "directx"], id: "offscreen", shape: CanaryBootShape.Offscreen),
        Manifest(backends: ["directx", "vulkan"], id: "windowed", shape: CanaryBootShape.Windowed),
        Manifest(id: "plain-windowed", shape: CanaryBootShape.Windowed),
        Manifest(id: "headless", shape: CanaryBootShape.Headless),
    ];
    private static (int ExitCode, string Output, string Error) Canary(params string[] arguments) =>
        ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["canary", .. arguments]));

    [Fact]
    public void OmittedSelectsBothBackendsAndANamedOneSelectsItAlone() {
        Assert.Equal(
            actual: CanaryCommand.SelectBackends(backend: null),
            expected: ["vulkan", "directx"]
        );
        Assert.Equal(
            actual: CanaryCommand.SelectBackends(backend: "vulkan"),
            expected: ["vulkan"]
        );
        Assert.Equal(
            actual: CanaryCommand.SelectBackends(backend: "directx"),
            expected: ["directx"]
        );
        Assert.Contains(
            actualString: Assert.Throws<ArgumentException>(testCode: static () => CanaryCommand.SelectBackends(backend: "d3d12")).Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "'d3d12'"
        );
    }
    // Only a manifest declaring backends is narrowed, each in its own authored order; a windowed manifest without
    // backends and a headless one boot once naming no backend, whatever the run selects.
    [Fact]
    public void OneBackendNarrowsOnlyTheProofsThatDeclareBackends() {
        Assert.Equal(
            actual: CanaryCommand.ExpandProofs(
                backends: CanaryCommand.SelectBackends(backend: null),
                manifests: Mixed()
            ).Select(selector: static proof => proof.Label),
            expected: ["offscreen on vulkan", "offscreen on directx", "windowed on directx", "windowed on vulkan", "plain-windowed", "headless"]
        );
        Assert.Equal(
            actual: CanaryCommand.ExpandProofs(
                backends: CanaryCommand.SelectBackends(backend: "directx"),
                manifests: Mixed()
            ).Select(selector: static proof => proof.Label),
            expected: ["offscreen on directx", "windowed on directx", "plain-windowed", "headless"]
        );
        Assert.Equal(
            actual: CanaryCommand.ExpandProofs(
                backends: CanaryCommand.SelectBackends(backend: "vulkan"),
                manifests: Mixed()
            ).Select(selector: static proof => proof.Label),
            expected: ["offscreen on vulkan", "windowed on vulkan", "plain-windowed", "headless"]
        );
    }
    [Fact]
    public void ThePlanOfOneBackendCountsHalfTheBackendDeclaringBoots() {
        var both = CanaryCommand.Plan(
            backends: CanaryCommand.SelectBackends(backend: null),
            manifests: Mixed(),
            namedWorldArtifact: false
        );
        var one = CanaryCommand.Plan(
            backends: CanaryCommand.SelectBackends(backend: "vulkan"),
            manifests: Mixed(),
            namedWorldArtifact: false
        );

        // Two boots per proof: four backend-declaring proofs plus two others, then two plus two.
        Assert.Equal(
            actual: both.WorldBoots,
            expected: 12
        );
        Assert.Equal(
            actual: one.WorldBoots,
            expected: 8
        );
    }
    [Fact]
    public void TheScopeNamesTheBackendsThatRanAndTheOneThatDidNot() {
        Assert.Equal(
            actual: CanaryCommand.BackendScope(proofs: CanaryCommand.ExpandProofs(
                backends: CanaryCommand.SelectBackends(backend: null),
                manifests: Mixed()
            )),
            expected: "on vulkan and directx"
        );
        Assert.Equal(
            actual: CanaryCommand.BackendScope(proofs: CanaryCommand.ExpandProofs(
                backends: CanaryCommand.SelectBackends(backend: "vulkan"),
                manifests: Mixed()
            )),
            expected: "on vulkan only (--backend vulkan), not on directx"
        );
        Assert.Equal(
            actual: CanaryCommand.BackendScope(proofs: CanaryCommand.ExpandProofs(
                backends: CanaryCommand.SelectBackends(backend: "directx"),
                manifests: Mixed()
            )),
            expected: "on directx only (--backend directx), not on vulkan"
        );
        // A run with no backend-declaring proof names no backend at all.
        Assert.Null(@object: CanaryCommand.BackendScope(proofs: CanaryCommand.ExpandProofs(
            backends: CanaryCommand.SelectBackends(backend: "vulkan"),
            manifests: [Manifest(id: "headless", shape: CanaryBootShape.Headless)]
        )));
    }
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
    [Fact]
    public void AnUnknownBackendIsRefusedByNameAtParse() {
        var (exitCode, _, error) = Canary("--capability", "offscreen", "--plan", "--backend", "d3d12");

        Assert.NotEqual(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(actualString: error, expectedSubstring: "'d3d12'");

        // Beside --merge the command's own check runs too, and must read the refused token rather than its value.
        var merge = Canary("--merge", "--plan", "--backend", "d3d12");

        Assert.NotEqual(
            actual: merge.ExitCode,
            expected: 0
        );
        Assert.Contains(actualString: merge.Error, expectedSubstring: "'d3d12'");
    }
    [Fact]
    public void TheMergeGateAndAListRefuseOneBackend() {
        var merge = Canary("--merge", "--plan", "--backend", "vulkan");
        var list = Canary("--list", "--backend", "directx");

        Assert.NotEqual(
            actual: merge.ExitCode,
            expected: 0
        );
        Assert.Contains(actualString: merge.Error, expectedSubstring: "--merge is the gate that holds both");
        Assert.NotEqual(
            actual: list.ExitCode,
            expected: 0
        );
        Assert.Contains(actualString: list.Error, expectedSubstring: "--list runs nothing");
    }
}
