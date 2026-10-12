using Puck.Testing;
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

namespace Puck.World.Games.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a <c>machine</c> screen whose content path names a <c>puck.cartridge.v1</c> document
/// compiles through the engine's own content provider at bind
/// (<c>WorldMachineCatalog.ContentProviders</c>) and boots the compiled image exactly as it boots a ROM
/// file; the slot pins the source's canonical hash beside the image's; a document the forge refuses faults the bind
/// with the forge's own message; a cartridge path on an engine with no forge refuses at validation by name; and the
/// shipped arcade module boots its three cabinets under alias <c>arcade</c> from a minimal host. Durable checkpoints
/// preserve each handheld's state and continuation, and refuse a mismatched inventory before changing the target.
/// </summary>
public sealed class MachineCartridgeLawTests {
    private const string AgbEngine = "advanced-gaming-brick";
    // There is deliberately no pinned frame hash here. Determinism pins the MAPPING, not the values (AGENTS rule 4):
    // a hash recorded from a past run is a historical value, and re-recording it is the only thing a forge or content
    // change can ever do to it — so it gates nothing and costs a chase every time the cabinet's game moves. What is
    // worth asserting is self-referential and lives below: the same document binds to the same image every time, and
    // the settled frame is a real picture rather than a blank one.
    /// <summary>The shipped cartridge these laws drive. They assert nothing about which game it is.</summary>
    private const string CartridgeFile = "hgb-mirror.cgb.cartridge.json";
    private const string CgbEngine = "gaming-brick";
    private const int MachineScreen = 8;
    private const int SettleFrames = 12;

    // The machine-host laws carry their OWN cartridges rather than reading whichever ones the game ships. A law
    // about binding, memory mirroring and symbol resolution is a law about the HOST; coupling it to shipped content
    // meant that retiring a cabinet's game broke ten host laws that had nothing to say about which game it was.
    private static string CartridgePath(string file) => Path.Combine(
        path1: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/cartridges"),
        path2: file
    );
    private static CartridgeCompilation CompileOutOfBand(string engine, string path) =>
        ((ICartridgeCompiler)TestMachines.Catalog().ContentProviders[engine]).Compile(document: CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: path)));
    // The compiled image's picture after SettleFrames frames from reset, on the forge's own verify driver, folded
    // FNV-1a over the pixel words; the distinct-pixel count rides along as the content gate (a blank frame never
    // reaches the pinned comparison).
    private static (ulong Hash, int DistinctPixels) FrameHash(CartridgeCompilation compilation) {
        var hash = 14695981039346656037UL;
        var distinct = new HashSet<uint>();

        if (compilation.Target == "agb") {
            using var driver = new AgbVerifyMachineDriver(
                rom: compilation.Rom,
                label: "law"
            );

            driver.RunFrames(
                frames: SettleFrames,
                keys: AgbKeys.None
            );

            for (var y = 0; (y < AdvancedMachineHost.ScreenHeight); y++) {
                for (var x = 0; (x < AdvancedMachineHost.ScreenWidth); x++) {
                    Fold(pixel: driver.ReadPixel(
                        x: x,
                        y: y
                    ));
                }
            }
        } else {
            using var driver = new VerifyMachineDriver(
                rom: compilation.Rom,
                label: "law"
            );

            driver.RunFrames(
                buttons: JoypadButtons.None,
                frames: SettleFrames
            );

            for (var y = 0; (y < MachineHost.ScreenHeight); y++) {
                for (var x = 0; (x < MachineHost.ScreenWidth); x++) {
                    Fold(pixel: driver.ReadPixel(
                        x: x,
                        y: y
                    ));
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
    private static string ModulePath() => RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/worlds/modules/arcade.puck");
    /// <summary>A cartridge the GAME ships, for the one law that boots a shipped module and must therefore stage
    /// what that module names.</summary>
    /// <param name="file">The cartridge file name.</param>
    /// <returns>The absolute path under the world's own assets.</returns>
    private static string ShippedCartridgePath(string file) => Path.Combine(
        path1: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/cartridges"),
        path2: file
    );
    private static long Slot(WorldDefinition definition, string name) {
        var row = WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: name
        );

        Assert.True(
            condition: (row is not null),
            userMessage: $"no row '{name}' among: {string.Join(
                separator: ",",
                values: definition.State.Select(selector: static row => row.Name.Value)
            )}"
        );

        return Assert.Single(
            collection: (row!.Cells ?? []),
            predicate: static cell => (cell.Key == WorldStateRow.SlotKey)
        ).Value.Raw;
    }
    private static WorldDefinition WithMachineScreen(string engine, string contentPath, string? options) {
        var document = Fixtures.BuildDocument();
        var configuration = ((engine == CgbEngine)
            ? JsonSerializer.SerializeToElement(new {
                schema = "puck.gaming-brick.configuration.v1",
                model = "cgb",
                boot = ((options?.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "fast"
            ) == true)
                ? "fast"
                : "cold"),
                content = new { path = contentPath }
            })
            : ((engine == "tune-instrument")
                ? JsonSerializer.SerializeToElement(new { schema = "puck.tune-instrument.configuration.v1", content = new { path = contentPath } })
                : JsonSerializer.SerializeToElement(new {
                    schema = "puck.advanced-gaming-brick.config.v1",
                    boot = ((options?.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: "fast"
                ) == true)
                    ? "fast"
                    : "cold"),
                    content = new { path = contentPath }
                })
        ));

        return document with {
            MachinesRaw = [.. document.Machines, new WorldMachine(
                "cabinet",
                engine,
                configuration
            )],
            ScreensRaw = [
                .. document.Screens,
                new WorldScreen(
                Index: MachineScreen,
                Origin: new Vector3(
                    x: 0f,
                    y: 1f,
                    z: 3f
                ),
                Right: new Vector3(
                    x: 1f,
                    y: 0f,
                    z: 0f
                ),
                Up: new Vector3(
                    x: 0f,
                    y: 1f,
                    z: 0f
                ),
                HalfWidth: 0.3f,
                HalfHeight: 0.27f,
                HalfDepth: 0.03f,
                Round: 0f,
                Source: new WorldScreenSource.Machine(
                    Instance: "cabinet",
                    Output: "video"
                ),
                Route: WorldScreenRoute.Passive
            ),
            ],
        };
    }
    [Fact]
    public void TheArcadeModuleBootsUnderItsAliasFromAMinimalHost() {
        using var files = new TemporaryDirectory();

        // The shipped layout: the host beside the worlds, the cartridges one directory up, so the module's own
        // module-relative content paths relocate into the host document exactly as they do under Assets/worlds.
        foreach (var file in ((ReadOnlySpan<string>)["hgb-mirror.cgb.cartridge.json", "pip.agb.cartridge.json"])) {
            _ = files.WriteBytes(
                name: Path.Combine(
                    path1: "cartridges",
                    path2: file
                ),
                bytes: File.ReadAllBytes(path: ShippedCartridgePath(file: file))
            );
        }

        var host = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes()))!.AsObject();
        var channels = host["channels"]!.AsArray();

        // The island's own channels the module's pad map and engage route read; the fixture declares the movement
        // three, so the host restates the two the arcade also names.
        channels.Add(value: new JsonObject { ["name"] = "jump", ["shape"] = "Binary", ["composition"] = true });
        channels.Add(value: new JsonObject { ["name"] = "rise", ["shape"] = "Bipolar", ["role"] = "MoveUp" });
        _ = files.WriteText(
            name: Path.Combine(
                path1: "worlds",
                path2: "modules",
                path3: "arcade.puck"
            ),
            text: File.ReadAllText(path: ModulePath())
        );
        host["imports"] = new JsonArray(new JsonObject { ["document"] = "modules/arcade", ["as"] = "arcade" });

        // The host's own body composes last and refines the import layer, and an empty list it authors replaces
        // the module's wholesale — so the sections the module owns are left to the module.
        foreach (var owned in ((ReadOnlySpan<string>)["state", "placements", "prototypes", "navigation", "machines"])) {
            _ = host.Remove(propertyName: owned);
        }

        var hostPath = files.WriteText(
            name: Path.Combine(
                path1: "worlds",
                path2: "host.world.json"
            ),
            text: host.ToJsonString()
        );

        var catalog = TestMachines.Catalog();

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path: hostPath,
                definition: out var loaded,
                reason: out var reason,
                catalog: catalog,
                catalogFingerprint: catalog.CompositionFingerprint
            ),
            userMessage: reason
        );
        Assert.NotNull(@object: loaded);

        var definition = loaded!;

        Assert.True(
            condition: WorldDefinitionFileSource.TryDescribeComposition(
                layers: out var layers,
                path: hostPath,
                reason: out var describeReason
            ),
            userMessage: describeReason
        );

        var layer = Assert.Single(
            collection: layers,
            predicate: static layer => (layer.Alias is not null)
        );

        Assert.Equal(
            actual: layer.Alias,
            expected: "arcade"
        );
        Assert.Equal(
            expected: "reads:cgbScreen,agbScreen,handheldScreen bindings:cgbScreen,agbScreen,handheldScreen",
            actual: layer.Exports!.Describe()
        );

        // The module's rows and placements compose under the alias; its kit and screens keep their bare names.
        Assert.Equal(
            expected: 8L,
            actual: Slot(
                definition: definition,
                name: "arcade$cgbScreen"
            )
        );
        Assert.Equal(
            expected: 9L,
            actual: Slot(
                definition: definition,
                name: "arcade$agbScreen"
            )
        );
        Assert.Equal(
            expected: 10L,
            actual: Slot(
                definition: definition,
                name: "arcade$handheldScreen"
            )
        );
        Assert.Contains(
            collection: definition.Placements.Select(selector: static placement => placement.Id),
            expected: "arcade$arcadeCourt"
        );
        Assert.Contains(
            collection: definition.SpawnPoints.Select(selector: static point => point.Id),
            expected: "arcade-arrival"
        );

        var population = new WorldPopulation(definition: definition);
        var machines = new WorldMachineHost(
            screens: definition.Screens,
            catalog: TestMachines.Catalog()
        );
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-world-tests-");
        var profiles = new WorldOwnedWorlds(
            template: definition,
            directory: stateDirectory.RootPath,
            machineId: Guid.NewGuid()
        );
        using var fixture = new WorldFixture(
            server: new WorldServer(
                definition: definition,
                population: population,
                profiles: profiles,
                envelope: new WorldRenderEnvelope(),
                machines: machines,
                narrationSink: new WorldConsoleNarrationSink()
            ),
            machines: machines
        );

        foreach (var (screen, engine, file) in ((ReadOnlySpan<(int, string, string)>)[(8, CgbEngine, "hgb-mirror.cgb.cartridge.json"), (9, AgbEngine, "pip.agb.cartridge.json"), (10, CgbEngine, "hgb-mirror.cgb.cartridge.json")])) {
            var state = fixture.Server.Machines.State(index: screen);

            Assert.NotNull(@object: state);
            Assert.True(
                condition: state!.Value.Assigned,
                userMessage: $"screen {screen}: {state.Value.Fault}"
            );
            Assert.Equal(
                expected: engine,
                actual: state.Value.Engine
            );
            Assert.Equal(
                expected: $"../cartridges/{file}",
                actual: state.Value.Cartridge!.Value.Path
            );
        }

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }
    }
}
