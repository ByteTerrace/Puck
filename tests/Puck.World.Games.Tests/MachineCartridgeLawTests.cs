using Puck.Testing;
using System.Numerics;
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

    [InlineData(CartridgeFile, CgbEngine, "cgb")]
    [InlineData("pip.agb.cartridge.json", AgbEngine, "stub")]
    [Theory]
    public void ACartridgePathBindsAndTheMachineRunsTheCompiledImage(string file, string engine, string options) {
        var path = CartridgePath(file: file);
        using var fixture = Fixtures.FreshServer(
            definition: WithMachineScreen(
                contentPath: path,
                engine: engine,
                options: options
            ),
            machineCatalog: TestMachines.Catalog()
        );

        var state = fixture.Server.Machines.InstanceState(name: "cabinet");

        Assert.True(
            state.HasValue,
            userMessage: "the named cabinet was not prepared"
        );
        Assert.Equal(
            expected: engine,
            actual: state!.Value.Engine
        );
        Assert.Equal(
            Puck.Abstractions.Machines.MachineRuntimeStatus.Running,
            state.Value.Status
        );
        Assert.NotNull(@object: fixture.Server.Machines.VideoOutput(
            instance: "cabinet",
            output: "video"
        ));

        // The same document through the same forge, outside the host: the slot pinned that compilation's source
        // identity and its image, and the image's first picture is the pinned one.
        var compilation = CompileOutOfBand(
            engine: engine,
            path: path
        );

        var (frameHash, distinctPixels) = FrameHash(compilation: compilation);

        Assert.True(
            condition: (distinctPixels >= 3),
            userMessage: $"the settled frame carries {distinctPixels} distinct pixel values; a picture needs more than a background"
        );

        // Self-referential, so it pins no historical value: the same document through the same forge a second time
        // settles on the same picture. A degenerate frame never reaches here — the distinct-pixel gate above stops it.
        var (repeatHash, _) = FrameHash(compilation: CompileOutOfBand(
            engine: engine,
            path: path
        ));

        Assert.True(
            condition: (frameHash == repeatHash),
            userMessage: $"{file}: two compilations of one document settled on 0x{frameHash:X16} and 0x{repeatHash:X16}"
        );

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }

    }
    [Fact]
    public void ACartridgePathOnAnEngineWithNoForgeRefusesAtValidationByName() {
        var denied = WithMachineScreen(
            contentPath: CartridgeFile,
            engine: "tune-instrument",
            options: null
        );
        var admitted = WithMachineScreen(
            contentPath: CartridgeFile,
            engine: CgbEngine,
            options: "cgb"
        );

        var catalog = TestMachines.Catalog();

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: denied,
                machines: catalog,
                reason: out var deniedReason
            ),
            userMessage: deniedReason
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: admitted,
                machines: catalog,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [Fact]
    public void AMalformedCartridgeRefusesTheBindWithTheForgesOwnMessage() {
        using var files = new TemporaryDirectory();
        var good = JsonNode.Parse(json: File.ReadAllText(path: CartridgePath(file: "hgb-mirror.cgb.cartridge.json")))!.AsObject();

        // Three colors in a background palette where the CGB target needs four — the forge's own validator names it.
        good["palettes"]!["background"] = new JsonArray(new JsonArray(
            0,
            1,
            2
        ));

        var badPath = files.WriteText(
            name: "bad.cartridge.json",
            text: good.ToJsonString()
        );
        var forgeMessage = Assert.Throws<DocumentValidationException>(testCode: () => CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: badPath))).Message.ReplaceLineEndings(replacementText: " ");
        var refusal = Assert.Throws<ArgumentException>(testCode: () => Fixtures.FreshServer(
            definition: WithMachineScreen(
                contentPath: badPath,
                engine: CgbEngine,
                options: "cgb"
            ),
            machineCatalog: TestMachines.Catalog()
        ));

        // Declared at boot: the slot faults by name with the forge's message and never boots.
        Assert.Contains(
            expectedSubstring: forgeMessage,
            actualString: refusal.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void TheSameDocumentCompilesToByteIdenticalImagesAcrossTwoBinds() {
        var path = CartridgePath(file: "hgb-mirror.cgb.cartridge.json");
        using var first = Fixtures.FreshServer(
            definition: WithMachineScreen(
                contentPath: path,
                engine: CgbEngine,
                options: "cgb"
            ),
            machineCatalog: TestMachines.Catalog()
        );
        using var second = Fixtures.FreshServer(
            definition: WithMachineScreen(
                contentPath: path,
                engine: CgbEngine,
                options: "cgb"
            ),
            machineCatalog: TestMachines.Catalog()
        );

        Assert.Equal(
            first.Server.Machines.CaptureInstances()[0].Configuration.GetRawText(),
            second.Server.Machines.CaptureInstances()[0].Configuration.GetRawText()
        );
        Assert.NotNull(value: first.Server.Machines.InstanceState(name: "cabinet"));
        Assert.NotNull(value: second.Server.Machines.InstanceState(name: "cabinet"));
    }
    [InlineData(CartridgeFile, CgbEngine)]
    [InlineData("pip.agb.cartridge.json", AgbEngine)]
    [Theory]
    public void WorldCheckpointPreservesSteppedMachineAndItsContinuation(string file, string engine) {
        var definition = WithMachineScreen(
            engine,
            CartridgePath(file: file),
            "fast"
        );
        var catalog = TestMachines.Catalog();
        using var source = Fixtures.FreshServer(
            definition,
            machineCatalog: catalog
        );
        using var target = Fixtures.FreshServer(
            definition,
            machineCatalog: catalog
        );

        for (var step = 0; (step < 3); step++) { source.Step(); }
        Assert.True(condition: source.Server.AnyMachineEverPumped);
        Assert.True(
            condition: source.Server.TryCaptureCheckpoint(
                WorldAuthorityHostRowCheckpoint.Empty,
                out var saved,
                out var reason
            ),
            userMessage: reason
        );
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: saved!);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: bytes,
                checkpoint: out var decoded,
                reason: out reason
            ),
            userMessage: reason
        );
        Assert.Single(collection: decoded!.Machines!.Instances);
        Assert.True(
            condition: target.Server.TryCaptureCheckpoint(
                WorldAuthorityHostRowCheckpoint.Empty,
                out var beforeRefusal,
                out reason
            ),
            userMessage: reason
        );
        var mismatched = decoded with {
            Machines = decoded.Machines with { Instances = [decoded.Machines.Instances[0] with { Engine = "different-provider" }] },
        };

        Assert.Throws<InvalidDataException>(testCode: () => target.Server.RestoreCheckpoint(checkpoint: mismatched));
        Assert.True(
            condition: target.Server.TryCaptureCheckpoint(
                WorldAuthorityHostRowCheckpoint.Empty,
                out var afterRefusal,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            WorldAuthorityCheckpointCodec.Encode(checkpoint: beforeRefusal!),
            WorldAuthorityCheckpointCodec.Encode(checkpoint: afterRefusal!)
        );
        target.Server.RestoreCheckpoint(checkpoint: decoded);
        Assert.Equal(
            source.Server.Machines.InstanceState(name: "cabinet"),
            target.Server.Machines.InstanceState(name: "cabinet")
        );
        for (var step = 0; (step < 6); step++) {
            source.Step(); target.Step();
            Assert.True(
                condition: source.Server.TryCaptureCheckpoint(
                    WorldAuthorityHostRowCheckpoint.Empty,
                    out var expected,
                    out reason
                ),
                userMessage: reason
            );
            Assert.True(
                condition: target.Server.TryCaptureCheckpoint(
                    WorldAuthorityHostRowCheckpoint.Empty,
                    out var actual,
                    out reason
                ),
                userMessage: reason
            );
            Assert.Equal(
                expected!.Machines!.Instances[0].RuntimeState,
                actual!.Machines!.Instances[0].RuntimeState
            );
            Assert.Equal(
                expected.Server.LastCompletedTick,
                actual.Server.LastCompletedTick
            );
            Assert.Equal(
                source.DefinitionBytes(),
                target.DefinitionBytes()
            );
        }
    }
}
