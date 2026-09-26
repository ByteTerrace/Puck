using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves what <c>puck canary --plan</c> counts: the World boots, runner-started processes, builds and summed leg
/// budget of a selection, from its manifests alone; which legs run alone; and that each gate selection is refused
/// once it outgrows its declared ceiling in <see cref="CanaryCeilings"/>.
/// </summary>
public sealed class CanaryPlanLawTests {
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
    private static CanaryManifest Manifest(string id, CanaryBootShape shape, int timeoutSeconds = 10, CanaryLeg? positive = null, CanaryLeg? discriminating = null, params string[] requirements) => new(
        Backends: ((shape == CanaryBootShape.Offscreen)
            ? ["vulkan", "directx"]
            : []),
        Binding: id,
        BootShape: shape,
        DirectoryPath: id,
        Discriminating: (discriminating ?? Leg(name: "discriminating")),
        Fixtures: [],
        Id: id,
        Positive: (positive ?? Leg(name: "positive")),
        Requirements: requirements,
        TimeoutSeconds: timeoutSeconds,
        Title: id
    );
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

    [Fact]
    public void EachLegShapeCountsTheProcessesItStartsAndTheTimeItMaySpend() {
        var package = new CanaryPackage(
            Alter: null,
            OutputName: "tint",
            SourcePath: "tint.graph.json"
        );
        var relaunch = new CanaryRelaunch(
            Commands: [],
            ScriptPath: "relaunch.script.txt",
            WorldFileName: "saved.world.json"
        );
        CanaryAuthorityRole[] mesh = [
            new(Id: "a", ScriptPath: "positive.script.txt", WorldPath: "world.json"),
            new(Id: "b", ScriptPath: "b.script.txt", WorldPath: "b.world.json"),
            new(Id: "c", ScriptPath: "c.script.txt", WorldPath: "c.world.json"),
        ];
        var plan = CanaryCommand.Plan(
            manifests: [
                Manifest(id: "lone", shape: CanaryBootShape.Headless),
                Manifest(id: "relaunch", shape: CanaryBootShape.Headless, positive: (Leg(name: "positive") with { Relaunch = relaunch })),
                Manifest(id: "companion", shape: CanaryBootShape.Headless, positive: (Leg(name: "positive") with { AuthorityWorldPath = "authority.world.json" }), discriminating: (Leg(name: "discriminating") with { AuthorityWorldPath = "authority.world.json" })),
                Manifest(id: "mesh", shape: CanaryBootShape.Headless, timeoutSeconds: 100, positive: (Leg(name: "positive") with { Authorities = mesh }), discriminating: (Leg(name: "discriminating") with { Authorities = mesh })),
                Manifest(id: "stub", shape: CanaryBootShape.Stub),
                Manifest(id: "offscreen", shape: CanaryBootShape.Offscreen, positive: (Leg(name: "positive") with { Package = package }), discriminating: (Leg(name: "discriminating") with { Package = (package with { Alter = "tint.hlsl" }) }), requirements: "gpu"),
            ],
            namedWorldArtifact: false,
            backends: WorldOffscreenLeg.Backends
        );

        Assert.Equal(
            expected: ["lone", "relaunch", "companion", "mesh", "stub", "offscreen on vulkan", "offscreen on directx"],
            actual: plan.Proofs.Select(selector: static proof => proof.Proof.Label)
        );
        Assert.Equal(
            expected: [2, 3, 4, 6, 3, 2, 2],
            actual: plan.Proofs.Select(selector: static proof => proof.WorldBoots)
        );
        Assert.Equal(
            expected: [2, 3, 4, 6, 0, 2, 2],
            actual: plan.Proofs.Select(selector: static proof => proof.WorldProcesses)
        );
        Assert.Equal(
            expected: [0, 0, 0, 0, 4, 0, 0],
            actual: plan.Proofs.Select(selector: static proof => proof.StubLaunches)
        );
        // Two timeouts per leg pair, doubled for a relaunch and again for a stub's two launches.
        Assert.Equal(
            expected: [20, 40, 20, 200, 40, 20, 20],
            actual: plan.Proofs.Select(selector: static proof => proof.BudgetSeconds)
        );
        Assert.Equal(
            expected: 14,
            actual: plan.Legs
        );
        Assert.Equal(
            expected: 4,
            actual: plan.ExclusiveLegs
        );
        Assert.Equal(
            expected: 22,
            actual: plan.WorldBoots
        );
        // Four legs across two backends name one source, so the run packages it once.
        Assert.Equal(
            expected: 1,
            actual: plan.PackageSpawns
        );
        Assert.Equal(
            expected: ((19 + 4) + 1),
            actual: plan.LegSpawns
        );
        Assert.Equal(
            expected: 2,
            actual: CanaryCommand.Plan(
                manifests: [
                    Manifest(id: "first", shape: CanaryBootShape.Offscreen, positive: (Leg(name: "positive") with { Package = package }), requirements: "gpu"),
                    Manifest(id: "second", shape: CanaryBootShape.Offscreen, positive: (Leg(name: "positive") with { Package = (package with { SourcePath = "other/tint.graph.json" }) }), requirements: "gpu"),
                ],
                namedWorldArtifact: false,
                backends: WorldOffscreenLeg.Backends
            ).PackageSpawns
        );
        Assert.Equal(
            expected: 1,
            actual: plan.StubBuilds
        );
        Assert.Equal(
            expected: 1,
            actual: plan.WorldBuilds
        );
        Assert.Equal(
            expected: 0,
            actual: CanaryCommand.Plan(manifests: [Manifest(id: "lone", shape: CanaryBootShape.Headless)], namedWorldArtifact: true, backends: WorldOffscreenLeg.Backends).WorldBuilds
        );
    }
    [Fact]
    public void ALegNeedingAMachineWideDeviceHoldsEverySlotAndNoOtherLegDoes() {
        const int Jobs = 6;

        foreach (var (manifest, exclusive) in (((CanaryManifest Manifest, bool Exclusive)[])[
            (Manifest(id: "headless", shape: CanaryBootShape.Headless), false),
            (Manifest(id: "stub", shape: CanaryBootShape.Stub), false),
            (Manifest(id: "windowed", shape: CanaryBootShape.Windowed), true),
            (Manifest(id: "offscreen", shape: CanaryBootShape.Offscreen, requirements: "gpu"), true),
            (Manifest(id: "headless-pad", shape: CanaryBootShape.Headless, requirements: "input:dualsense"), true),
            (Manifest(id: "headless-audio", shape: CanaryBootShape.Headless, requirements: "audio-output"), true),
        ])) {
            Assert.Equal(
                expected: exclusive,
                actual: CanaryCommand.IsExclusive(manifest: manifest)
            );
            Assert.Equal(
                expected: (exclusive
                    ? Jobs
                    : 1),
                actual: CanaryCommand.LegWeight(
                    jobs: Jobs,
                    leg: manifest.Positive,
                    manifest: manifest
                )
            );
        }
    }
    [Fact]
    public void ACeilingRefusesOnlyAPlanThatExceedsItAndNamesWhereToRaiseIt() {
        var plan = CanaryCommand.Plan(
            manifests: [Manifest(id: "lone", shape: CanaryBootShape.Headless, timeoutSeconds: 30)],
            namedWorldArtifact: false,
            backends: WorldOffscreenLeg.Backends
        );

        Assert.Null(@object: CanaryCommand.CeilingRefusal(
            ceiling: new CanaryCeiling(LegBudgetSeconds: 60, WorldBoots: 2),
            name: "Merge",
            plan: plan
        ));

        var boots = CanaryCommand.CeilingRefusal(
            ceiling: new CanaryCeiling(LegBudgetSeconds: 60, WorldBoots: 1),
            name: "Merge",
            plan: plan
        );

        Assert.NotNull(@object: boots);
        Assert.Contains(actualString: boots, expectedSubstring: "2 World boots against 1");
        Assert.Contains(actualString: boots, expectedSubstring: "CanaryCeilings.Merge");
        Assert.Contains(actualString: boots, expectedSubstring: "src/Puck.Cli/Canary/CanaryCeilings.cs");
        Assert.DoesNotContain(actualString: boots, expectedSubstring: "leg budget");
        Assert.Contains(
            expectedSubstring: "a 60-second leg budget against 59",
            actualString: CanaryCommand.CeilingRefusal(
                ceiling: new CanaryCeiling(LegBudgetSeconds: 59, WorldBoots: 2),
                name: "Automatic",
                plan: plan
            )
        );
    }
    [Fact]
    public void TheShippedGateSelectionsFitTheirDeclaredCeilings() {
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
            ceiling: CanaryCeilings.Automatic,
            name: nameof(CanaryCeilings.Automatic),
            plan: automatic
        ));
        Assert.Null(@object: CanaryCommand.CeilingRefusal(
            ceiling: CanaryCeilings.Merge,
            name: nameof(CanaryCeilings.Merge),
            plan: merge
        ));
    }
    [Fact]
    public void PlanningTheMergeGatePrintsTheSameCountsEveryTimeWithoutRunningAnything() {
        var first = ConsoleCapture.RunSplit(run: static () => PuckRootCommand.Invoke(args: ["canary", "--merge", "--plan"]));
        var second = ConsoleCapture.RunSplit(run: static () => PuckRootCommand.Invoke(args: ["canary", "--merge", "--plan"]));
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
    [Fact]
    public void PlanningAListIsRefusedAtParse() {
        var (exitCode, _, error) = ConsoleCapture.RunSplit(run: static () => PuckRootCommand.Invoke(args: ["canary", "--list", "--plan"]));

        Assert.NotEqual(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(actualString: error, expectedSubstring: "--plan counts a run and --list runs nothing");
    }
}
