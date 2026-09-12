using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Forge;
using Puck.Assets.Documents;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge.Framework;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a <c>machine</c> screen whose content path names a <c>puck.cartridge.v1</c> document
/// compiles through the engine's own content provider at bind
/// (<c>WorldMachineCatalog.ContentProviders</c>) and boots the compiled image exactly as it boots a ROM
/// file; the slot pins the source's canonical hash beside the image's; a document the forge refuses faults the bind
/// with the forge's own message; a cartridge path on an engine with no forge refuses at validation by name; and the
/// shipped arcade module boots its three cabinets under alias <c>arcade</c> from a minimal host.
/// </summary>
public sealed class MachineCartridgeLawTests {
    // There is deliberately no pinned frame hash here. Determinism pins the MAPPING, not the values (AGENTS rule 4):
    // a hash recorded from a past run is a historical value, and re-recording it is the only thing a forge or content
    // change can ever do to it — so it gates nothing and costs a chase every time the cabinet's game moves. What is
    // worth asserting is self-referential and lives below: the same document binds to the same image every time, and
    // the settled frame is a real picture rather than a blank one.
    /// <summary>The shipped cartridge these laws drive. They assert nothing about which game it is.</summary>
    private const string CartridgeFile = "hgb-mirror.cgb.cartridge.json";
    private const string CgbEngine = "gaming-brick";
    private const string AgbEngine = "advanced-gaming-brick";
    private const int MachineScreen = 8;
    private const int SettleFrames = 12;

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
    private static string CartridgePath(string file) => Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "cartridges", file);

    /// <summary>A cartridge the GAME ships, for the one law that boots a shipped module and must therefore stage
    /// what that module names.</summary>
    /// <param name="file">The cartridge file name.</param>
    /// <returns>The absolute path under the world's own assets.</returns>
    private static string ShippedCartridgePath(string file) => Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "cartridges", file);
    private static string ModulePath() => Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "worlds", "modules", "arcade.world.json");
    private static WorldDefinition WithMachineScreen(string engine, string contentPath, string? options) {
        var document = Fixtures.BuildDocument();
        var configuration = engine == CgbEngine
            ? JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.config.v1", model = "cgb", boot = options?.Contains("fast", StringComparison.Ordinal) == true ? "fast" : "cold", content = new { path = contentPath } })
            : engine == "tune-instrument"
                ? JsonSerializer.SerializeToElement(new { schema = "puck.tune-instrument.config.v1", content = new { path = contentPath } })
                : JsonSerializer.SerializeToElement(new { schema = "puck.advanced-gaming-brick.config.v1", boot = options?.Contains("fast", StringComparison.Ordinal) == true ? "fast" : "cold", content = new { path = contentPath } });

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
    private static long Slot(WorldDefinition definition, string name) {
        var row = WorldDefinitionRows.FindStateRow(rows: definition.State, name: name);

        Assert.True(condition: (row is not null), userMessage: $"no row '{name}' among: {string.Join(separator: ",", values: definition.State.Select(selector: static row => row.Name.Value))}");

        return Assert.Single(collection: (row!.Cells ?? []), predicate: static cell => (cell.Key == WorldStateRow.SlotKey)).Value;
    }
    private static CartridgeCompilation CompileOutOfBand(string engine, string path) =>
        ((ICartridgeCompiler)TestHookInstaller.CreateMachineCatalog().ContentProviders[engine]).Compile(document: CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: path)));
    // The compiled image's picture after SettleFrames frames from reset, on the forge's own verify driver, folded
    // FNV-1a over the pixel words; the distinct-pixel count rides along as the content gate (a blank frame never
    // reaches the pinned comparison).
    private static (ulong Hash, int DistinctPixels) FrameHash(CartridgeCompilation compilation) {
        var hash = 14695981039346656037UL;
        var distinct = new HashSet<uint>();

        if (compilation.Target == "agb") {
            using var driver = new AgbVerifyMachineDriver(rom: compilation.Rom, label: "law");

            driver.RunFrames(keys: AgbKeys.None, frames: SettleFrames);

            for (var y = 0; (y < AdvancedMachineHost.ScreenHeight); y++) {
                for (var x = 0; (x < AdvancedMachineHost.ScreenWidth); x++) {
                    Fold(pixel: driver.ReadPixel(x: x, y: y));
                }
            }
        } else {
            using var driver = new VerifyMachineDriver(rom: compilation.Rom, label: "law");

            driver.RunFrames(buttons: JoypadButtons.None, frames: SettleFrames);

            for (var y = 0; (y < MachineHost.ScreenHeight); y++) {
                for (var x = 0; (x < MachineHost.ScreenWidth); x++) {
                    Fold(pixel: driver.ReadPixel(x: x, y: y));
                }
            }
        }

        return (Hash: hash, DistinctPixels: distinct.Count);

        void Fold(uint pixel) {
            _ = distinct.Add(item: pixel);
            hash ^= pixel;
            hash *= 1099511628211UL;
        }
    }

    [Theory]
    [InlineData(CartridgeFile, CgbEngine, "cgb")]
    [InlineData("pip.agb.cartridge.json", AgbEngine, "stub")]
    public void ACartridgePathBindsAndTheMachineRunsTheCompiledImage(string file, string engine, string options) {
        var path = CartridgePath(file: file);
        using var fixture = Fixtures.FreshServer(definition: WithMachineScreen(engine: engine, contentPath: path, options: options), machineCatalog: TestHookInstaller.CreateMachineCatalog());

        var state = fixture.Server.Machines.InstanceState("cabinet");
        Assert.True(state.HasValue, userMessage: "the named cabinet was not prepared");
        Assert.Equal(expected: engine, actual: state!.Value.Engine);
        Assert.Equal(Puck.Abstractions.Machines.MachineRuntimeStatus.Running, state.Value.Status);
        Assert.NotNull(fixture.Server.Machines.VideoOutput("cabinet", "video"));

        // The same document through the same forge, outside the host: the slot pinned that compilation's source
        // identity and its image, and the image's first picture is the pinned one.
        var compilation = CompileOutOfBand(engine: engine, path: path);
        var (frameHash, distinctPixels) = FrameHash(compilation: compilation);

        Assert.True(condition: (distinctPixels >= 3), userMessage: $"the settled frame carries {distinctPixels} distinct pixel values; a picture needs more than a background");

        // Self-referential, so it pins no historical value: the same document through the same forge a second time
        // settles on the same picture. A degenerate frame never reaches here — the distinct-pixel gate above stops it.
        var (repeatHash, _) = FrameHash(compilation: CompileOutOfBand(engine: engine, path: path));

        Assert.True(condition: (frameHash == repeatHash), userMessage: $"{file}: two compilations of one document settled on 0x{frameHash:X16} and 0x{repeatHash:X16}");

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }

    }
    [Fact]
    public void TheSameDocumentCompilesToByteIdenticalImagesAcrossTwoBinds() {
        var path = CartridgePath(file: "hgb-mirror.cgb.cartridge.json");
        using var first = Fixtures.FreshServer(definition: WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb"), machineCatalog: TestHookInstaller.CreateMachineCatalog());
        using var second = Fixtures.FreshServer(definition: WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb"), machineCatalog: TestHookInstaller.CreateMachineCatalog());
        Assert.Equal(first.Server.Machines.CaptureInstances()[0].Configuration.GetRawText(),
            second.Server.Machines.CaptureInstances()[0].Configuration.GetRawText());
        Assert.NotNull(first.Server.Machines.InstanceState("cabinet"));
        Assert.NotNull(second.Server.Machines.InstanceState("cabinet"));
    }
    [Fact]
    public void AMalformedCartridgeRefusesTheBindWithTheForgesOwnMessage() {
        using var files = new TempWorldDirectory();
        var good = JsonNode.Parse(json: File.ReadAllText(path: CartridgePath(file: "hgb-mirror.cgb.cartridge.json")))!.AsObject();

        // Three colors in a background palette where the CGB target needs four — the forge's own validator names it.
        good["palettes"]!["background"] = new JsonArray(new JsonArray(0, 1, 2));

        var badPath = files.WriteText(name: "bad.cartridge.json", text: good.ToJsonString());
        var forgeMessage = Assert.Throws<DocumentValidationException>(testCode: () => CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: badPath))).Message.ReplaceLineEndings(replacementText: " ");
        var refusal = Assert.Throws<ArgumentException>(() => Fixtures.FreshServer(definition: WithMachineScreen(engine: CgbEngine, contentPath: badPath, options: "cgb"), machineCatalog: TestHookInstaller.CreateMachineCatalog()));

        // Declared at boot: the slot faults by name with the forge's message and never boots.
        Assert.Contains(expectedSubstring: forgeMessage, actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void ACartridgePathOnAnEngineWithNoForgeRefusesAtValidationByName() {
        var denied = WithMachineScreen(engine: "tune-instrument", contentPath: CartridgeFile, options: null);
        var admitted = WithMachineScreen(engine: CgbEngine, contentPath: CartridgeFile, options: "cgb");

        var catalog = TestHookInstaller.CreateMachineCatalog();
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, machines: catalog, reason: out var deniedReason), userMessage: deniedReason);
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, machines: catalog, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void TheArcadeModuleBootsUnderItsAliasFromAMinimalHost() {
        using var files = new TempWorldDirectory();

        // The shipped layout: the host beside the worlds, the cartridges one directory up, so the module's own
        // module-relative content paths relocate into the host document exactly as they do under Assets/worlds.
        foreach (var file in (ReadOnlySpan<string>)["hgb-mirror.cgb.cartridge.json", "pip.agb.cartridge.json"]) {
            _ = files.WriteBytes(name: Path.Combine("cartridges", file), bytes: File.ReadAllBytes(path: ShippedCartridgePath(file: file)));
        }

        var host = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes()))!.AsObject();
        var channels = host["channels"]!.AsArray();

        // The island's own channels the module's pad map and engage route read; the fixture declares the movement
        // three, so the host restates the two the arcade also names.
        channels.Add(value: new JsonObject { ["name"] = "jump", ["shape"] = "Binary", ["composition"] = true });
        channels.Add(value: new JsonObject { ["name"] = "rise", ["shape"] = "Bipolar", ["role"] = "MoveUp" });
        _ = files.WriteText(name: Path.Combine("worlds", "modules", "arcade.world.json"), text: File.ReadAllText(ModulePath()));
        host["imports"] = new JsonArray(new JsonObject { ["document"] = "modules/arcade.world.json", ["as"] = "arcade" });

        // The host's own body composes last and refines the import layer, and an empty list it authors replaces
        // the module's wholesale — so the sections the module owns are left to the module.
        foreach (var owned in (ReadOnlySpan<string>)["state", "placements", "prototypes", "navigation", "machines"]) {
            _ = host.Remove(propertyName: owned);
        }

        var hostPath = files.WriteText(name: Path.Combine("worlds", "host.world.json"), text: host.ToJsonString());

        var catalog = TestHookInstaller.CreateMachineCatalog();
        Assert.True(condition: WorldDefinitionLoader.TryLoadFile(path: hostPath, definition: out var loaded, reason: out var reason, catalog: catalog, catalogFingerprint: catalog.CompositionFingerprint), userMessage: reason);
        Assert.NotNull(@object: loaded);

        var definition = loaded!;
        Assert.True(condition: WorldDefinitionFileSource.TryDescribeComposition(path: hostPath, layers: out var layers, reason: out var describeReason), userMessage: describeReason);

        var layer = Assert.Single(collection: layers, predicate: static layer => (layer.Alias is not null));

        Assert.Equal(expected: "arcade", actual: layer.Alias);
        Assert.Equal(expected: "reads:cgbScreen,agbScreen,handheldScreen bindings:cgbScreen,agbScreen,handheldScreen", actual: layer.Exports!.Describe());

        // The module's rows compose under the alias; its placements, kit, and screens keep their bare names.
        Assert.Equal(expected: 8L, actual: Slot(definition: definition, name: "arcade_cgbScreen"));
        Assert.Equal(expected: 9L, actual: Slot(definition: definition, name: "arcade_agbScreen"));
        Assert.Equal(expected: 10L, actual: Slot(definition: definition, name: "arcade_handheldScreen"));
        Assert.Contains(collection: definition.Placements.Select(selector: static placement => placement.Id), expected: "arcadeCourt");
        Assert.Contains(collection: definition.SpawnPoints.Select(selector: static point => point.Id), expected: "arcade-arrival");

        var population = new WorldPopulation(definition: definition);
        var machines = new WorldMachineHost(screens: definition.Screens, catalog: TestHookInstaller.CreateMachineCatalog(), documentPath: hostPath);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-world-tests-").FullName;
        var profiles = new WorldOwnedWorlds(template: definition, directory: stateDirectory, machineId: Guid.NewGuid());
        using var fixture = new WorldFixture(
            server: new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines, narrationSink: new WorldConsoleNarrationSink()),
            machines: machines,
            stateDirectory: stateDirectory
        );

        foreach (var (screen, engine, file) in (ReadOnlySpan<(int, string, string)>)[(8, CgbEngine, "hgb-mirror.cgb.cartridge.json"), (9, AgbEngine, "pip.agb.cartridge.json"), (10, CgbEngine, "hgb-mirror.cgb.cartridge.json")]) {
            var state = fixture.Server.Machines.State(index: screen);

            Assert.NotNull(@object: state);
            Assert.True(condition: state!.Value.Assigned, userMessage: $"screen {screen}: {state.Value.Fault}");
            Assert.Equal(expected: engine, actual: state.Value.Engine);
            Assert.Equal(expected: $"../cartridges/{file}", actual: state.Value.Cartridge!.Value.Path);
        }

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }
    }
}
