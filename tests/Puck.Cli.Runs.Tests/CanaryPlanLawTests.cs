using Puck.Cli.Canary;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// Proves what <c>puck canary --plan</c> counts: the World boots, runner-started processes, builds and summed leg
/// budget of a selection, from its manifests alone, and which legs run alone.
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
    private static CanaryManifest Manifest(string id, CanaryBootShape shape, int timeoutSeconds = 10, CanaryLeg? positive = null, CanaryLeg? discriminating = null, bool exclusive = false, params string[] requirements) => new(
        Backends: ((shape == CanaryBootShape.Offscreen)
            ? ["vulkan", "directx"]
            : []),
        Binding: id,
        BootShape: shape,
        DirectoryPath: id,
        Discriminating: (discriminating ?? Leg(name: "discriminating")),
        Exclusive: exclusive,
        Fixtures: [],
        Id: id,
        Positive: (positive ?? Leg(name: "positive")),
        Requirements: requirements,
        TimeoutSeconds: timeoutSeconds,
        Title: id
    );

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
                Manifest(id: "mesh", shape: CanaryBootShape.Headless, timeoutSeconds: 100, positive: (Leg(name: "positive") with { Authorities = mesh }), discriminating: (Leg(name: "discriminating") with { Authorities = mesh }), exclusive: true),
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
            expected: [2, 3, 4, 6, 4, 2, 2],
            actual: plan.Proofs.Select(selector: static proof => proof.WorldBoots)
        );
        Assert.Equal(
            expected: [2, 3, 4, 6, 0, 2, 2],
            actual: plan.Proofs.Select(selector: static proof => proof.WorldProcesses)
        );
        Assert.Equal(
            expected: [0, 0, 0, 0, 5, 0, 0],
            actual: plan.Proofs.Select(selector: static proof => proof.StubLaunches)
        );
        // Two timeouts per leg pair, doubled for a relaunch; a stub proof observes five launches.
        Assert.Equal(
            expected: [20, 40, 20, 200, 50, 20, 20],
            actual: plan.Proofs.Select(selector: static proof => proof.BudgetSeconds)
        );
        Assert.Equal(
            expected: 14,
            actual: plan.Legs
        );
        // The declared mesh runs alone; the offscreen proof's legs, one per backend, each hold a GPU slot.
        Assert.Equal(
            expected: 2,
            actual: plan.ExclusiveLegs
        );
        Assert.Equal(
            expected: 4,
            actual: plan.GpuLegs
        );
        Assert.Equal(
            expected: 23,
            actual: plan.WorldBoots
        );
        // Four legs across two backends name one source, so the run packages it once.
        Assert.Equal(
            expected: 1,
            actual: plan.PackageSpawns
        );
        Assert.Equal(
            expected: ((19 + 5) + 1),
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
    /// <summary>A warm boot succeeds only when it exits 0 within its timeout after reporting the engine ready, landing
    /// its capture through the display encode, so the encode is ready in the cache every leg starts from, and printing
    /// its backend's pipeline-cache counts, the ready and capture lines narrated on standard error; a timeout, a nonzero
    /// exit, a missing ready line, a capture that never landed (refused, or another file's) or missing counts is
    /// refused, naming the backend.</summary>
    [Fact]
    public void AWarmBootThatTimesOutExitsNonzeroOrNeverGetsReadyIsRefusedByItsBackend() {
        const string EngineReady = "[engine: ready at tick 12]\n";
        const string Ready = (EngineReady + "[capture] main -> D:\\run\\vulkan-encode.png\n");
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
        Assert.Null(@object: Refusal(backend: "directx", stderr: (EngineReady + "[capture] world -> D:\\run\\directx-encode.png\n"), stdout: Counts));
        Assert.Contains(expectedSubstring: "warm on vulkan from plain's positive world never landed its capture, so the display encode's pipeline was not built", actualString: Refusal(backend: "vulkan", stderr: EngineReady, stdout: Counts));
        Assert.Contains(expectedSubstring: "never landed its capture", actualString: Refusal(backend: "vulkan", stderr: (EngineReady + "[capture] refused D:\\run\\vulkan-encode.png: the run ended before any frame served it\n"), stdout: Counts));
        Assert.Contains(expectedSubstring: "never landed its capture", actualString: Refusal(backend: "directx", stderr: Ready, stdout: Counts));
    }
    /// <summary>A leg starts from exactly the files the warm boots persisted, and counts as having built no pipeline
    /// outside them only when its cache holds those same files, byte for byte, after it exits: a changed file or an
    /// added one (a device the warm never saw) does not count.</summary>
    [Fact]
    public void AWarmedCacheSeedsEachLegAndCountsOnlyTheLegsThatWroteNothingBack() {
        using var scratch = new TemporaryDirectory(prefix: "puck-cache-seed-");
        var root = scratch.RootPath;

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
    }
    [Fact]
    public void OnlyAManifestThatDeclaresItRunsAloneAndEveryGraphicsDeviceLegHoldsAGpuSlot() {
        foreach (var (manifest, exclusive, gpu) in (((CanaryManifest Manifest, bool Exclusive, bool Gpu)[])[
            (Manifest(id: "headless", shape: CanaryBootShape.Headless), false, false),
            (Manifest(id: "stub", shape: CanaryBootShape.Stub), false, false),
            (Manifest(id: "windowed", shape: CanaryBootShape.Windowed), false, true),
            (Manifest(id: "offscreen", shape: CanaryBootShape.Offscreen, requirements: "gpu"), false, true),
            (Manifest(id: "headless-gpu", shape: CanaryBootShape.Headless, requirements: "gpu"), false, true),
            (Manifest(id: "headless-pad", shape: CanaryBootShape.Headless, requirements: "input:dualsense"), false, false),
            (Manifest(id: "headless-audio", shape: CanaryBootShape.Headless, requirements: "audio-output"), false, false),
            (Manifest(id: "declared", shape: CanaryBootShape.Headless, exclusive: true), true, false),
            (Manifest(id: "declared-offscreen", shape: CanaryBootShape.Offscreen, exclusive: true, requirements: "gpu"), true, true),
        ])) {
            var proof = Assert.Single(collection: CanaryCommand.Plan(
                backends: ["vulkan"],
                manifests: [manifest],
                namedWorldArtifact: true
            ).Proofs);

            Assert.Equal(
                expected: (manifest.Id, exclusive, gpu),
                actual: (manifest.Id, proof.Exclusive, proof.Gpu)
            );
        }
    }
    [Fact]
    public void PlanningAListIsRefusedAtParse() {
        var (exitCode, _, error) = ConsoleCapture.RunSplit(run: static () => SuiteRoot.Invoke(args: ["canary", "--list", "--plan"]));

        Assert.NotEqual(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(actualString: error, expectedSubstring: "--plan counts a run and --list runs nothing");
    }
}
