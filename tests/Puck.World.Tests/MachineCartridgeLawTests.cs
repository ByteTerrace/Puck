using System.Numerics;
using System.Text;
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
/// (<see cref="WorldScreenSource.Machine.CartridgeDocumentSuffix"/>) compiles through the engine's own forge at bind
/// (<see cref="WorldScreenMachineEngines.CartridgeCompilers"/>) and boots the compiled image exactly as it boots a ROM
/// file; the slot pins the source's canonical hash beside the image's; a document the forge refuses faults the bind
/// with the forge's own message; a cartridge path on an engine with no forge refuses at validation by name; and the
/// shipped arcade module boots its three cabinets under alias <c>arcade</c> from a minimal host.
/// </summary>
public sealed class MachineCartridgeLawTests {
    // The two shipped cartridges' framebuffers after a fixed number of frames from reset, folded pixel by pixel
    // (FNV-1a over the packed pixel words) — the compiled image's own first picture, pinned so a forge change that
    // moves what the cabinet shows moves this law with it.
    private const ulong AgbFirstFrameHash = 0x4B38410EDDF2A409UL;
    private const ulong CgbFirstFrameHash = 0x1AAA9EC68E388985UL;
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
    private static string CartridgePath(string file) => Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "cartridges", file);
    private static string ModulePath() => Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "worlds", "modules", "arcade.world.json");
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
    private static long Slot(WorldDefinition definition, string name) {
        var row = WorldDefinitionRows.FindStateRow(rows: definition.State, name: name);

        Assert.True(condition: (row is not null), userMessage: $"no row '{name}' among: {string.Join(separator: ",", values: definition.State.Select(selector: static row => row.Name.Value))}");

        return Assert.Single(collection: (row!.Cells ?? []), predicate: static cell => (cell.Key == WorldStateRow.SlotKey)).Value;
    }
    private static CartridgeCompilation CompileOutOfBand(string engine, string path) =>
        WorldScreenMachineEngines.CartridgeCompilers[engine].Compile(document: CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: path)));
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
    [InlineData("pip.cgb.cartridge.json", CgbEngine, "cgb", CgbFirstFrameHash)]
    [InlineData("pip.agb.cartridge.json", AgbEngine, "stub", AgbFirstFrameHash)]
    public void ACartridgePathBindsAndTheMachineRunsTheCompiledImage(string file, string engine, string options, ulong pinnedFrameHash) {
        var path = CartridgePath(file: file);
        using var fixture = Fixtures.FreshServer(definition: WithMachineScreen(engine: engine, contentPath: path, options: options), engines: WorldScreenMachineEngines.All);

        Assert.True(condition: fixture.Server.Machines.HasMachine(index: MachineScreen), userMessage: fixture.Server.Machines.State(index: MachineScreen)?.Fault);

        var state = fixture.Server.Machines.State(index: MachineScreen)!.Value;

        Assert.Null(@object: state.Fault);
        Assert.Equal(expected: engine, actual: state.Engine);
        Assert.NotNull(@object: state.Cartridge);

        // The same document through the same forge, outside the host: the slot pinned that compilation's source
        // identity and its image, and the image's first picture is the pinned one.
        var compilation = CompileOutOfBand(engine: engine, path: path);
        var (frameHash, distinctPixels) = FrameHash(compilation: compilation);

        Assert.Equal(expected: path, actual: state.Cartridge.Value.Path);
        Assert.Equal(expected: compilation.SourceHash, actual: state.Cartridge.Value.SourceHash);
        Assert.Equal(expected: WorldDefinitionFileSource.ComputeContentHash(content: compilation.Rom), actual: state.Cartridge.Value.RomHash);
        Assert.True(condition: (distinctPixels >= 3), userMessage: $"the settled frame carries {distinctPixels} distinct pixel values; the room needs its floor, a wall, and the pip");
        Assert.True(condition: (frameHash == pinnedFrameHash), userMessage: $"{file}: settled frame hash 0x{frameHash:X16}, pinned 0x{pinnedFrameHash:X16}");

        // The booted machine is running that image: the cartridge header's title, peeked through the seam, is the
        // document's own (the AGB host answers no memory peek, so its proof rests on the pinned image hash alone).
        if (fixture.Server.Machines.TryPeek(screen: MachineScreen, address: 0x0134, value: out var first)) {
            Assert.Equal(expected: (byte)'P', actual: first);
        }

        for (var tick = 0; (tick < 8); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.Machines.State(index: MachineScreen)!.Value.Assigned);
    }
    [Fact]
    public void TheSameDocumentCompilesToByteIdenticalImagesAcrossTwoBinds() {
        var path = CartridgePath(file: "pip.cgb.cartridge.json");
        using var first = Fixtures.FreshServer(definition: WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb"), engines: WorldScreenMachineEngines.All);
        using var second = Fixtures.FreshServer(definition: WithMachineScreen(engine: CgbEngine, contentPath: path, options: "cgb"), engines: WorldScreenMachineEngines.All);
        var declared = first.Server.Machines.State(index: MachineScreen)!.Value.Cartridge;
        var again = second.Server.Machines.State(index: MachineScreen)!.Value.Cartridge;

        Assert.NotNull(@object: declared);
        Assert.Equal(expected: declared, actual: again);

        // A live re-insert of the same document lands the same image, and says so in its accept line.
        var (ok, message, contentHash) = first.Server.Machines.TryInsert(index: MachineScreen, contentPath: path, engineId: CgbEngine, options: "cgb");

        Assert.True(condition: ok, userMessage: message);
        Assert.Equal(expected: WorldDefinitionFileSource.ComputeContentHash(content: File.ReadAllBytes(path: path)), actual: contentHash);
        Assert.Contains(expectedSubstring: $"cartridge hash {declared!.Value.SourceHash} rom {declared.Value.RomHash}", actualString: message, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: declared, actual: first.Server.Machines.State(index: MachineScreen)!.Value.Cartridge);
        Assert.Equal(expected: WorldDefinitionFileSource.ComputeContentHash(content: CompileOutOfBand(engine: CgbEngine, path: path).Rom), actual: declared.Value.RomHash);
    }
    [Fact]
    public void AMalformedCartridgeRefusesTheBindWithTheForgesOwnMessage() {
        using var files = new TempWorldDirectory();
        var good = JsonNode.Parse(json: File.ReadAllText(path: CartridgePath(file: "pip.cgb.cartridge.json")))!.AsObject();

        // Three colors in a background palette where the CGB target needs four — the forge's own validator names it.
        good["palettes"]!["background"] = new JsonArray(new JsonArray(0, 1, 2));

        var badPath = files.WriteText(name: "bad.cartridge.json", text: good.ToJsonString());
        var forgeMessage = Assert.Throws<DocumentValidationException>(testCode: () => CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: badPath))).Message.ReplaceLineEndings(replacementText: " ");
        using var fixture = Fixtures.FreshServer(definition: WithMachineScreen(engine: CgbEngine, contentPath: badPath, options: "cgb"), engines: WorldScreenMachineEngines.All);

        // Declared at boot: the slot faults by name with the forge's message and never boots.
        Assert.False(condition: fixture.Server.Machines.HasMachine(index: MachineScreen));
        Assert.Equal(expected: $"cartridge '{badPath}' refused: {forgeMessage}", actual: fixture.Server.Machines.State(index: MachineScreen)!.Value.Fault);

        // Inserted live: the same refusal, the source file still pinned for the tape.
        var (ok, message, contentHash) = fixture.Server.Machines.TryInsert(index: MachineScreen, contentPath: badPath, engineId: CgbEngine, options: "cgb");

        Assert.False(condition: ok);
        Assert.Equal(expected: $"cartridge '{badPath}' refused: {forgeMessage}", actual: message);
        Assert.Equal(expected: WorldDefinitionFileSource.ComputeContentHash(content: File.ReadAllBytes(path: badPath)), actual: contentHash);
        Assert.False(condition: fixture.Server.Machines.HasMachine(index: MachineScreen));

        // The control: the shipped document inserts onto the same slot.
        var (controlOk, controlMessage, _) = fixture.Server.Machines.TryInsert(index: MachineScreen, contentPath: CartridgePath(file: "pip.cgb.cartridge.json"), engineId: CgbEngine, options: "cgb");

        Assert.True(condition: controlOk, userMessage: controlMessage);
        Assert.True(condition: fixture.Server.Machines.HasMachine(index: MachineScreen));
    }
    [Fact]
    public void ACartridgePathOnAnEngineWithNoForgeRefusesAtValidationByName() {
        var denied = WithMachineScreen(engine: "tune-instrument", contentPath: "pip.cgb.cartridge.json", options: null);
        var admitted = WithMachineScreen(engine: CgbEngine, contentPath: "pip.cgb.cartridge.json", options: "cgb");

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(expectedSubstring: "screens[1].source.machine.contentPath 'pip.cgb.cartridge.json' names a cartridge document (.cartridge.json), but engine 'tune-instrument' compiles none.", actualString: deniedReason, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void TheArcadeModuleBootsUnderItsAliasFromAMinimalHost() {
        using var files = new TempWorldDirectory();

        // The shipped layout: the host beside the worlds, the cartridges one directory up, so the module's own
        // "../cartridges/" spellings resolve against the host document exactly as they do under Assets/worlds.
        foreach (var file in (ReadOnlySpan<string>)["pip.cgb.cartridge.json", "pip.agb.cartridge.json"]) {
            _ = files.WriteBytes(name: Path.Combine("cartridges", file), bytes: File.ReadAllBytes(path: CartridgePath(file: file)));
        }

        var host = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes()))!.AsObject();
        var channels = host["channels"]!.AsArray();

        // The island's own channels the module's pad map and engage route read; the fixture declares the movement
        // three, so the host restates the two the arcade also names.
        channels.Add(value: new JsonObject { ["name"] = "jump", ["shape"] = "Binary", ["composition"] = true });
        channels.Add(value: new JsonObject { ["name"] = "rise", ["shape"] = "Bipolar", ["role"] = "MoveUp" });
        host["imports"] = new JsonArray(new JsonObject { ["document"] = ModulePath(), ["as"] = "arcade" });

        // The host's own body composes last and refines the import layer, and an empty list it authors replaces
        // the module's wholesale — so the sections the module owns are left to the module.
        foreach (var owned in (ReadOnlySpan<string>)["state", "placements", "prototypes", "navigation"]) {
            _ = host.Remove(propertyName: owned);
        }

        var hostPath = files.WriteText(name: Path.Combine("worlds", "host.world.json"), text: host.ToJsonString());

        Assert.True(condition: WorldDefinitionLoader.TryLoadFile(path: hostPath, definition: out var loaded, reason: out var reason), userMessage: reason);
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
        var machines = new WorldMachineHost(screens: definition.Screens, engines: WorldScreenMachineEngines.All, documentPath: hostPath);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-world-tests-").FullName;
        var profiles = new WorldOwnedWorlds(template: definition, directory: stateDirectory, machineId: Guid.NewGuid());
        using var fixture = new WorldFixture(
            server: new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines, narrationSink: new WorldConsoleNarrationSink()),
            machines: machines,
            stateDirectory: stateDirectory
        );

        foreach (var (screen, engine, file) in (ReadOnlySpan<(int, string, string)>)[(8, CgbEngine, "pip.cgb.cartridge.json"), (9, AgbEngine, "pip.agb.cartridge.json"), (10, CgbEngine, "pip.cgb.cartridge.json")]) {
            var state = fixture.Server.Machines.State(index: screen);

            Assert.NotNull(@object: state);
            Assert.True(condition: state!.Value.Assigned, userMessage: $"screen {screen}: {state.Value.Fault}");
            Assert.Equal(expected: engine, actual: state.Value.Engine);
            Assert.Equal(expected: $"../cartridges/{file}", actual: state.Value.Cartridge!.Value.Path);
        }

        for (var tick = 0; (tick < 4); tick++) {
            fixture.Step();
        }
    }
}
