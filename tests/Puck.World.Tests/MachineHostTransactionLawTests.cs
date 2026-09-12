using System.Numerics;
using System.Text.Json;
using Puck.GamingBricks.Forge;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldMachineHost"/> supports two-phase transactional prepare/commit/finish
/// identical to the addon lifecycle, symbol-aware memory peeking through cartridge variable symbol maps,
/// and dynamic registration of screen-machine engines and cartridge compilers.
/// </summary>
public sealed class MachineHostTransactionLawTests {
    private const string CgbEngine = "gaming-brick";
    private const int MachineScreen = 8;

    private static string RepoRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }

    // The machine-host laws carry their OWN cartridges rather than reading whichever ones the game ships. A law
    // about binding, memory mirroring and symbol resolution is a law about the HOST; coupling it to shipped content
    // meant that retiring a cabinet's game broke ten host laws that had nothing to say about which game it was.
    /// <summary>The shipped cartridge these laws drive. They assert nothing about which game it is.</summary>
    private const string CartridgeFile = "hgb-mirror.cgb.cartridge.json";

    private static string CartridgePath(string file) => Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "cartridges", file);

    private static WorldDefinition WithMachineScreen(string engine, string contentPath, string? options) {
        var document = Fixtures.BuildDocument();
        var configuration = JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.config.v1", model = "cgb", boot = "fast", content = new { path = contentPath } });

        return document with {
            MachinesRaw = [.. document.Machines, new WorldMachine("cabinet", engine, configuration)],
            ScreensRaw = [
                .. document.Screens,
                new WorldScreen(
                    Index: MachineScreen,
                    Origin: new Vector3(x: 0f, y: 1f, z: 3f),
                    Right: new Vector3(x: 1f, y: 0f, z: 0f),
                    Up: new Vector3(x: 0f, y: 1f, z: 0f),
                    HalfWidth: 0.3f,
                    HalfHeight: 0.27f,
                    HalfDepth: 0.03f,
                    Round: 0f,
                    Source: new WorldScreenSource.Machine("cabinet", "video"),
                    Route: WorldScreenRoute.Passive
                ),
            ],
        };
    }

    [Fact]
    public void WorldMachineCatalog_ExposesEnginesAndContentProviders() {
        var catalog = TestHookInstaller.CreateMachineCatalog();
        Assert.True(condition: catalog.IsRegistered(engineId: "gaming-brick"));
        Assert.True(condition: catalog.IsRegistered(engineId: "advanced-gaming-brick"));
        Assert.True(condition: catalog.IsRegistered(engineId: "tune-instrument"));
        Assert.False(condition: catalog.IsRegistered(engineId: "unknown-xyz"));

        Assert.True(condition: catalog.ContentProviders.ContainsKey(key: "gaming-brick"));
        Assert.True(condition: catalog.ContentProviders.ContainsKey(key: "advanced-gaming-brick"));
        Assert.False(condition: catalog.ContentProviders.ContainsKey(key: "tune-instrument"));

        Assert.True(condition: TestHookInstaller.CreateMachineCatalog().ContentProviders.ContainsKey(key: "gaming-brick"));
        Assert.True(condition: TestHookInstaller.CreateMachineCatalog().ContentProviders.ContainsKey(key: "advanced-gaming-brick"));
        Assert.False(condition: TestHookInstaller.CreateMachineCatalog().ContentProviders.ContainsKey(key: "tune-instrument"));
    }

    [Fact]
    public void TryResolveSymbol_ResolvesNamedVariablesOnCompiledCartridge() {
        // Every symbol and every byte below is READ OUT OF the cartridge rather than written into the law. What is
        // under test is that the host resolves a declared name to a distinct address and that a peek at it finds the
        // value the cartridge's own reset code put there — neither of which is a fact about which game is in the
        // cabinet. Spelling the game's own numbers here is what made these laws break every time the cabinet changed.
        var path = CartridgePath(file: CartridgeFile);
        var declared = CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: path)).Variables;

        Assert.True(condition: (declared.Length >= 2), userMessage: "the cartridge declares too few variables to tell two addresses apart");

        using var fixture = Fixtures.FreshServer(definition: WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb fast"), machineCatalog: TestHookInstaller.CreateMachineCatalog());

        Assert.NotNull(fixture.Server.Machines.InstanceState("cabinet"));

        var addresses = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var variable in declared) {
            Assert.True(
                condition: fixture.Server.Machines.TryResolveSymbol("cabinet", variable.Name, out var address),
                userMessage: $"'{variable.Name}' is declared but did not resolve");
            Assert.True(condition: (address > 0), userMessage: $"'{variable.Name}' resolved to {address}");

            addresses[variable.Name] = address;
        }

        // Distinct names occupy distinct bytes.
        Assert.Equal(expected: declared.Length, actual: addresses.Values.Distinct().Count());

        // A name the cartridge does not declare resolves to nothing.
        Assert.False(condition: fixture.Server.Machines.TryResolveSymbol("cabinet", "nonexistent_variable", out _));

        // Fast boot skips firmware presentation, but the cartridge still runs its own initialization.
        _ = Fixtures.StepUntil(fixture, ceiling: 120, settled: () => declared.All(variable =>
            fixture.Server.Machines.Inspect("cabinet", new("bus", checked((ulong)addresses[variable.Name]), 1)).Status == Puck.Abstractions.Machines.MachineAccessStatus.Available &&
            fixture.Server.Machines.Inspect("cabinet", new("bus", checked((ulong)addresses[variable.Name]), 1)).Value == (ulong)variable.Initial));

        // Once the reset code has run, each byte carries the value the document authored for it.
        foreach (var variable in declared) {
            Assert.True(
                condition: fixture.Server.Machines.Inspect("cabinet", new("bus", checked((ulong)addresses[variable.Name]), 1)).Status == Puck.Abstractions.Machines.MachineAccessStatus.Available,
                userMessage: $"'{variable.Name}' could not be peeked");
            Assert.Equal(expected: variable.Initial, actual: (byte)fixture.Server.Machines.Inspect("cabinet", new("bus", checked((ulong)addresses[variable.Name]), 1)).Value);
        }
    }

    [Fact]
    public void TwoPhasePrepareAndCommit_AppliesCandidateDefinition() {
        var path = CartridgePath(file: CartridgeFile);
        var baseDef = Fixtures.BuildDocument();
        var candidateDef = WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb fast");
        var host = new WorldMachineHost(screens: [], catalog: TestHookInstaller.CreateMachineCatalog(), documentPath: path);

        Assert.Null(host.InstanceState("cabinet"));

        // Phase 1: Prepare
        Assert.True(condition: host.TryPrepare(current: baseDef, candidate: candidateDef, plan: out var plan, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: plan);

        // Before commit, screen is not yet active
        Assert.Null(host.InstanceState("cabinet"));

        // Phase 2: Commit & Finish
        host.Commit(plan: plan!);
        host.Finish(plan: plan!);

        // After commit, screen is active
        Assert.NotNull(host.InstanceState("cabinet"));
        var state = host.InstanceState("cabinet");
        Assert.NotNull(@object: state);
        Assert.Equal(expected: CgbEngine, actual: state!.Value.Engine);
    }

    [Fact]
    public void TwoPhasePrepareRollback_DisposedWithoutCommitLeavesHostUnmodified() {
        var path = CartridgePath(file: CartridgeFile);
        var baseDef = Fixtures.BuildDocument();
        var candidateDef = WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb fast");
        var host = new WorldMachineHost(screens: [], catalog: TestHookInstaller.CreateMachineCatalog(), documentPath: path);

        Assert.True(condition: host.TryPrepare(current: baseDef, candidate: candidateDef, plan: out var plan, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: plan);

        // Dispose without committing (Rollback)
        plan!.Dispose();

        // Screen was never activated
        Assert.Null(host.InstanceState("cabinet"));
    }

}
