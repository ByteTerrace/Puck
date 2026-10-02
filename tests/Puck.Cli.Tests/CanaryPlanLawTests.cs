using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves what <c>puck canary --plan</c> counts: the World boots, runner-started processes, builds and summed leg
/// budget of a selection, from its manifests alone; which legs run alone; and that each gate selection is refused
/// once it outgrows the cost recorded in <see cref="CanaryCeilingsLedger"/>.
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
    /// <summary>A selection with GPU proofs boots the first plain offscreen proof's positive world once per backend its
    /// GPU proofs boot, before any leg, and counts those boots and their timeouts in its plan; a selection without a GPU
    /// proof, or without a plain offscreen positive leg to boot, warms nothing.</summary>
    [Fact]
    public void AGpuSelectionWarmsThePipelineCacheOncePerBackendBeforeAnyLeg() {
        var packaged = (Leg(name: "positive") with { Package = new CanaryPackage(Alter: null, OutputName: "tint", SourcePath: "tint.graph.json") });
        CanaryManifest[] manifests = [
            Manifest(id: "lone", shape: CanaryBootShape.Headless),
            Manifest(id: "packaged", shape: CanaryBootShape.Offscreen, positive: packaged, requirements: "gpu"),
            Manifest(id: "plain", shape: CanaryBootShape.Offscreen, timeoutSeconds: 60, requirements: "gpu"),
            Manifest(id: "windowed", shape: CanaryBootShape.Windowed),
        ];
        var plan = CanaryCommand.Plan(
            backends: WorldOffscreenLeg.Backends,
            manifests: manifests,
            namedWorldArtifact: false
        );

        Assert.NotNull(@object: plan.Warm);
        Assert.Equal(expected: "plain", actual: plan.Warm.Manifest.Id);
        Assert.Equal(expected: WorldOffscreenLeg.Backends, actual: plan.Warm.Backends);
        Assert.Equal(expected: WorldOffscreenLeg.Backends.Count, actual: plan.WarmBoots);
        Assert.Equal(
            expected: (plan.Proofs.Sum(selector: static proof => proof.WorldBoots) + plan.WarmBoots),
            actual: plan.WorldBoots
        );
        Assert.Equal(
            expected: (plan.Proofs.Sum(selector: static proof => proof.WorldProcesses) + plan.WarmBoots),
            actual: plan.WorldProcesses
        );
        Assert.Equal(
            expected: (plan.Proofs.Sum(selector: static proof => proof.BudgetSeconds) + (plan.WarmBoots * CanaryCommand.WarmSeconds)),
            actual: plan.BudgetSeconds
        );
        Assert.Equal(
            expected: ["vulkan"],
            actual: CanaryCommand.Plan(backends: ["vulkan"], manifests: manifests, namedWorldArtifact: false).Warm!.Backends
        );
        Assert.Null(@object: CanaryCommand.Plan(backends: WorldOffscreenLeg.Backends, manifests: [manifests[0], manifests[3]], namedWorldArtifact: false).Warm);
        Assert.Null(@object: CanaryCommand.Plan(backends: WorldOffscreenLeg.Backends, manifests: [manifests[1]], namedWorldArtifact: false).Warm);
        Assert.Equal(
            expected: 0,
            actual: CanaryCommand.Plan(backends: WorldOffscreenLeg.Backends, manifests: [manifests[0]], namedWorldArtifact: false).WarmBoots
        );
    }
    /// <summary>A warm boot succeeds only when it exits 0 within its timeout after reporting the engine ready and
    /// printing its backend's pipeline-cache counts, the ready line narrated on standard error; a timeout, a nonzero exit, a missing ready line or missing counts
    /// is refused, naming the backend.</summary>
    [Fact]
    public void AWarmBootThatTimesOutExitsNonzeroOrNeverGetsReadyIsRefusedByItsBackend() {
        const string Ready = "[engine: ready at tick 12]\n";
        const string Counts = "[world.counters: pipeline-cache.vulkan\n  gpu.created.pipelines 14\n  gpu.pipeline-cache.hits 0\n  gpu.pipeline-cache.misses 14\n";

        static string? Refusal(string backend, string stdout, string stderr = Ready, int exitCode = 0, bool timedOut = false) => CanaryCommand.WarmRefusal(
            backend: backend,
            process: new CliProcessResult(ExitCode: exitCode, OutputLines: [], Stderr: stderr, Stdout: stdout, TimedOut: timedOut),
            source: "plain"
        );

        Assert.Null(@object: Refusal(backend: "vulkan", stdout: Counts));
        Assert.Contains(expectedSubstring: "warm on vulkan from plain's positive world did not exit within its 180-second timeout", actualString: Refusal(backend: "vulkan", exitCode: -1, stdout: string.Empty, timedOut: true));
        Assert.Contains(expectedSubstring: "warm on directx from plain's positive world exited 3", actualString: Refusal(backend: "directx", exitCode: 3, stdout: Counts));
        Assert.Contains(expectedSubstring: "warm on vulkan from plain's positive world never reported the engine ready", actualString: Refusal(backend: "vulkan", stderr: string.Empty, stdout: Counts));
        Assert.Contains(expectedSubstring: "warm on vulkan from plain's positive world never reported the engine ready", actualString: Refusal(backend: "vulkan", stderr: string.Empty, stdout: (Ready + Counts)));
        Assert.Contains(expectedSubstring: "printed no pipeline-cache.vulkan counts", actualString: Refusal(backend: "vulkan", stdout: string.Empty));
    }
    /// <summary>A run whose warm boot cannot start a World fails the selection with exit 2, naming the backend, and
    /// starts no leg.</summary>
    [Fact]
    public void AFailedWarmFailsTheSelectionByNameBeforeAnyLeg() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-warm-refusal-").FullName;

        try {
            var artifact = Path.Combine(path1: root, path2: "Puck.World.dll");

            File.WriteAllBytes(bytes: [], path: artifact);

            var (exitCode, output, error) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["canary", "sdf-mesh-motion", "--backend", "vulkan", "--world-artifact", artifact]));
            // Every assertion names the whole run, so a failure shows what the verb wrote instead of what it did not.
            var transcript = $"exit {exitCode}{Environment.NewLine}--- stdout ---{Environment.NewLine}{output}--- stderr ---{Environment.NewLine}{error}";

            Assert.True(condition: (exitCode == CliExit.Refused), userMessage: transcript);
            Assert.True(condition: error.Contains(comparisonType: StringComparison.Ordinal, value: "ERROR: the pipeline-cache warm on vulkan from sdf-mesh-motion's positive world exited"), userMessage: transcript);
            Assert.True(condition: error.Contains(comparisonType: StringComparison.Ordinal, value: "The selection fails without starting a leg."), userMessage: transcript);
            Assert.False(condition: output.Contains(comparisonType: StringComparison.Ordinal, value: "sdf-mesh-motion on vulkan positive"), userMessage: transcript);
        } finally {
            Directory.Delete(path: root, recursive: true);
        }
    }
    /// <summary>A leg starts from exactly the files the warm boots persisted, and counts as having built no pipeline
    /// outside them only when its cache holds those same files, byte for byte, after it exits: a changed file or an
    /// added one (a device the warm never saw) does not count.</summary>
    [Fact]
    public void AWarmedCacheSeedsEachLegAndCountsOnlyTheLegsThatWroteNothingBack() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-cache-seed-").FullName;

        try {
            var warmed = Path.Combine(path1: root, path2: "warm", path3: "pipeline-cache");
            var cache = Path.Combine(path1: warmed, path2: "vulkan", path3: "device");

            Directory.CreateDirectory(path: cache);
            File.WriteAllBytes(path: Path.Combine(path1: cache, path2: "key.bin"), bytes: [1, 2, 3]);
            File.WriteAllBytes(path: Path.Combine(path1: cache, path2: "key.bin.tmp"), bytes: [9]);

            var seed = new CanaryCommand.CanaryPipelineCacheSeed();

            Assert.False(condition: seed.TrySeed(stateDirectory: Path.Combine(path1: root, path2: "empty")));
            seed.Capture(directory: warmed);

            string[] legs = [.. Enumerable.Range(count: 3, start: 0).Select(selector: index => Path.Combine(path1: root, path2: $"leg{index}", path3: "state"))];

            foreach (var leg in legs) {
                Assert.True(condition: seed.TrySeed(stateDirectory: leg));
                Assert.Equal(expected: [1, 2, 3], actual: File.ReadAllBytes(path: Path.Combine(paths: [leg, "pipeline-cache", "vulkan", "device", "key.bin"])));
                Assert.False(condition: File.Exists(path: Path.Combine(paths: [leg, "pipeline-cache", "vulkan", "device", "key.bin.tmp"])));
            }

            File.WriteAllBytes(path: Path.Combine(paths: [legs[1], "pipeline-cache", "vulkan", "device", "key.bin"]), bytes: [1, 2, 3, 4]);
            Directory.CreateDirectory(path: Path.Combine(paths: [legs[2], "pipeline-cache", "directx", "device"]));
            File.WriteAllBytes(path: Path.Combine(paths: [legs[2], "pipeline-cache", "directx", "device", "key.bin"]), bytes: [5]);

            foreach (var leg in legs) {
                seed.Observe(stateDirectory: leg);
            }

            Assert.Equal(expected: (3, 1), actual: (seed.Seeded, seed.Unchanged));
        } finally {
            Directory.Delete(path: root, recursive: true);
        }
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
            name: "merge",
            plan: plan
        ));

        var boots = CanaryCommand.CeilingRefusal(
            ceiling: new CanaryCeiling(LegBudgetSeconds: 60, WorldBoots: 1),
            name: "merge",
            plan: plan
        );

        Assert.NotNull(@object: boots);
        Assert.Contains(actualString: boots, expectedSubstring: "2 World boots against 1");
        Assert.Contains(actualString: boots, expectedSubstring: "merge in CanaryCeilings.json");
        Assert.Contains(actualString: boots, expectedSubstring: "puck canary-ceilings");
        Assert.DoesNotContain(actualString: boots, expectedSubstring: "leg budget");
        Assert.Contains(
            expectedSubstring: "a 60-second leg budget against 59",
            actualString: CanaryCommand.CeilingRefusal(
                ceiling: new CanaryCeiling(LegBudgetSeconds: 59, WorldBoots: 2),
                name: "automatic",
                plan: plan
            )
        );
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
