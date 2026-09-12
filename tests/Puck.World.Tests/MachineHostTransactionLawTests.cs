using System.Numerics;
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
    private static string CartridgePath(string file) => Path.Combine(RepoRoot(), "tests", "Puck.World.Tests", "Fixtures", "cartridges", file);

    private static WorldDefinition WithMachineScreen(string engine, string contentPath, string? options) {
        var document = Fixtures.BuildDocument();

        return document with {
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
                    Source: new WorldScreenSource.Machine(Engine: engine, ContentPath: contentPath, Options: options),
                    Route: WorldScreenRoute.Passive
                ),
            ],
        };
    }

    [Fact]
    public void WorldScreenMachineEngines_ExposesEnginesAndCompilers() {
        Assert.True(condition: WorldScreenMachineEngines.IsRegistered(key: "gaming-brick"));
        Assert.True(condition: WorldScreenMachineEngines.IsRegistered(key: "advanced-gaming-brick"));
        Assert.True(condition: WorldScreenMachineEngines.IsRegistered(key: "tune-instrument"));
        Assert.False(condition: WorldScreenMachineEngines.IsRegistered(key: "unknown-xyz"));

        Assert.True(condition: WorldScreenMachineEngines.CompilesCartridges(key: "gaming-brick"));
        Assert.True(condition: WorldScreenMachineEngines.CompilesCartridges(key: "advanced-gaming-brick"));
        Assert.False(condition: WorldScreenMachineEngines.CompilesCartridges(key: "tune-instrument"));

        Assert.True(condition: WorldScreenMachineEngines.CartridgeCompilers.ContainsKey(key: "gaming-brick"));
        Assert.True(condition: WorldScreenMachineEngines.CartridgeCompilers.ContainsKey(key: "advanced-gaming-brick"));
        Assert.False(condition: WorldScreenMachineEngines.CartridgeCompilers.ContainsKey(key: "tune-instrument"));
    }

    [Fact]
    public void TryResolveSymbol_ResolvesNamedVariablesOnCompiledCartridge() {
        var path = CartridgePath(file: "pip.cgb.cartridge.json");
        using var fixture = Fixtures.FreshServer(definition: WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb"), engines: WorldScreenMachineEngines.All);

        Assert.True(condition: fixture.Server.Machines.HasMachine(index: MachineScreen));

        // pip defines variables "x" (initial 76), "y" (initial 68), "steps" (initial 0)
        Assert.True(condition: fixture.Server.Machines.TryResolveSymbol(index: MachineScreen, symbol: "x", address: out var xAddress));
        Assert.True(condition: (xAddress > 0));

        Assert.True(condition: fixture.Server.Machines.TryResolveSymbol(index: MachineScreen, symbol: "y", address: out var yAddress));
        Assert.True(condition: (yAddress > 0));
        Assert.NotEqual(expected: xAddress, actual: yAddress);

        Assert.True(condition: fixture.Server.Machines.TryResolveSymbol(index: MachineScreen, symbol: "steps", address: out var stepsAddress));
        Assert.True(condition: (stepsAddress > 0));

        // Unknown symbol fails
        Assert.False(condition: fixture.Server.Machines.TryResolveSymbol(index: MachineScreen, symbol: "nonexistent_variable", address: out _));

        for (var tick = 0; (tick < 8); tick++) {
            fixture.Step();
        }

        // Peek resolves initial variable value directly from emulator memory once game code initializes WRAM
        Assert.True(condition: fixture.Server.Machines.TryPeek(screen: MachineScreen, address: xAddress, value: out var xValue));
        Assert.Equal(expected: 76, actual: xValue);

        Assert.True(condition: fixture.Server.Machines.TryPeek(screen: MachineScreen, address: yAddress, value: out var yValue));
        Assert.Equal(expected: 68, actual: yValue);
    }

    [Fact]
    public void TwoPhasePrepareAndCommit_AppliesCandidateDefinition() {
        var path = CartridgePath(file: "pip.cgb.cartridge.json");
        var baseDef = Fixtures.BuildDocument();
        var candidateDef = WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb");
        var host = new WorldMachineHost(screens: [], engines: WorldScreenMachineEngines.All, documentPath: path);

        Assert.False(condition: host.HasMachine(index: MachineScreen));

        // Phase 1: Prepare
        Assert.True(condition: host.TryPrepare(current: baseDef, candidate: candidateDef, plan: out var plan, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: plan);

        // Before commit, screen is not yet active
        Assert.False(condition: host.HasMachine(index: MachineScreen));

        // Phase 2: Commit & Finish
        host.Commit(plan: plan!);
        host.Finish(plan: plan!);

        // After commit, screen is active
        Assert.True(condition: host.HasMachine(index: MachineScreen));
        var state = host.State(index: MachineScreen);
        Assert.NotNull(@object: state);
        Assert.Equal(expected: CgbEngine, actual: state!.Value.Engine);
    }

    [Fact]
    public void TwoPhasePrepareRollback_DisposedWithoutCommitLeavesHostUnmodified() {
        var path = CartridgePath(file: "pip.cgb.cartridge.json");
        var baseDef = Fixtures.BuildDocument();
        var candidateDef = WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb");
        var host = new WorldMachineHost(screens: [], engines: WorldScreenMachineEngines.All, documentPath: path);

        Assert.True(condition: host.TryPrepare(current: baseDef, candidate: candidateDef, plan: out var plan, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: plan);

        // Dispose without committing (Rollback)
        plan!.Dispose();

        // Screen was never activated
        Assert.False(condition: host.HasMachine(index: MachineScreen));
    }
}
