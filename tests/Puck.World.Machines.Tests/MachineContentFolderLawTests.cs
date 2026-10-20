using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.Networking;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Machines.Tests;

/// <summary>
/// THE LAW: a machine's authored <c>contentFolder</c> is the whole of what it can swap to. <c>machine.content</c> lists
/// the files directly in that folder the machine's engine can mount, resolved beside the declaring document, and marks
/// the one it runs; <c>machine.swap</c> mounts one of those by file name or unambiguous stem, and refuses any other name.
/// An engine that prepares content through a provider lists only what the provider recognizes; one that mounts raw
/// bytes lists every file.
/// </summary>
public sealed class MachineContentFolderLawTests {
    private sealed class FixtureAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }
    // The fixture's instance carries a federation identity no peer ever exercises.
    private sealed class UnusedAuthenticator : IAuthenticator {
        public int ChallengeBytes => 0;
        public bool IsConfigured => false;

        public byte[] NewChallenge() => [];
        public byte[] Prove(ReadOnlySpan<byte> challenge) => throw new NotSupportedException();
        public bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority) {
            sourceAuthority = null;

            return false;
        }
    }

    private static string Shipped(string relativePath) => RepositoryPaths.Resolve(relativePath: $"src/Puck.World/Assets/{relativePath}");
    // A document directory beside a cartridge folder holding two compiled cartridges, a source the provider does not
    // recognize, and a third cartridge sharing the second's stem.
    private static TemporaryDirectory Layout() {
        var root = new TemporaryDirectory(
            bestEffortDelete: true,
            prefix: "puck-content-folder-"
        );
        var carts = root.PathOf(name: "carts");

        _ = Directory.CreateDirectory(path: root.PathOf(name: "world"));
        _ = Directory.CreateDirectory(path: carts);
        File.Copy(destFileName: Path.Combine(path1: carts, path2: "mirror.cgb.cartridge.json"), sourceFileName: Shipped(relativePath: "cartridges/cgb/hgb-mirror.cgb.cartridge.json"));
        File.Copy(destFileName: Path.Combine(path1: carts, path2: "gun.cgb.cartridge.json"), sourceFileName: Shipped(relativePath: "cartridges/cgb/light-gun.cgb.cartridge.json"));
        File.Copy(destFileName: Path.Combine(path1: carts, path2: "gun.dmg.cartridge.json"), sourceFileName: Shipped(relativePath: "cartridges/cgb/light-gun.cgb.cartridge.json"));
        File.Copy(destFileName: Path.Combine(path1: carts, path2: "mirror.cgb.puck"), sourceFileName: Shipped(relativePath: "cartridges/cgb/light-gun.cgb.puck"));

        return root;
    }
    private static WorldDefinition WithCabinet(string? contentFolder) {
        var document = Fixtures.BuildDocument();

        return document with {
            MachinesRaw = [.. document.Machines, new WorldMachine(
                "cabinet",
                "gaming-brick",
                JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.configuration.v1", model = "cgb", boot = "fast", content = new { path = "../carts/mirror.cgb.cartridge.json" } }),
                ContentFolder: contentFolder
            )],
            ScreensRaw = [
                .. document.Screens,
                new WorldScreen(
                    Index: 8,
                    Origin: new Vector3(x: 0f, y: 1f, z: 3f),
                    Right: Vector3.UnitX,
                    Up: Vector3.UnitY,
                    HalfWidth: 0.3f,
                    HalfHeight: 0.27f,
                    HalfDepth: 0.03f,
                    Round: 0f,
                    Source: new WorldScreenSource.Machine(Instance: "cabinet", Output: "video"),
                    Route: WorldScreenRoute.Passive
                ),
            ],
        };
    }
    private static (WorldFixture Fixture, CommandRegistry Registry) Boot(TemporaryDirectory root, string? contentFolder) {
        var fixture = Fixtures.FreshServer(
            definition: WithCabinet(contentFolder: contentFolder),
            documentPath: Path.Combine(path1: root.PathOf(name: "world"), path2: "cabinet.world.json"),
            machineCatalog: TestMachines.Catalog()
        );
        var instance = new WorldInstance(
            documentOrigin: new WorldFileOrigin(resolvedPath: "cabinet"),
            federation: new WorldFederationIdentity(Authenticator: new UnusedAuthenticator(), Subject: fixture.Server.AuthorityIdentity),
            link: new LoopbackTransport(server: fixture.Server),
            name: "cabinet",
            origin: static () => "cabinet",
            ownedMachines: null,
            server: fixture.Server
        );

        return (fixture, new CommandRegistry(modules: [new WorldMachineCommandModule(authority: new FixtureAuthority(instance: instance))]));
    }

    [Fact]
    public void TheFolderListsWhatTheEngineMountsAndASwapMountsItByName() {
        using var root = Layout();
        var (fixture, registry) = Boot(contentFolder: "../carts", root: root);

        using (fixture) {
            var listed = registry.Submit(line: "machine.content cabinet");

            Assert.False(condition: listed.IsError, userMessage: listed.Output);
            Assert.Contains(expectedSubstring: "folder=../carts", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "mounted=../carts/mirror.cgb.cartridge.json entries=3", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "| mirror.cgb.cartridge.json mounted=true", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "| gun.cgb.cartridge.json mounted=false", actualString: listed.Output);
            // The source beside the compiled cartridge is not content the gaming brick's provider prepares.
            Assert.DoesNotContain(expectedSubstring: ".puck", actualString: listed.Output);

            var swapped = registry.Submit(line: "machine.swap cabinet gun.cgb.cartridge.json");

            Assert.False(condition: swapped.IsError, userMessage: swapped.Output);
            Assert.Contains(expectedSubstring: "status=Applied", actualString: swapped.Output);
            Assert.Contains(expectedSubstring: "| gun.cgb.cartridge.json mounted=true", actualString: registry.Submit(line: "machine.content cabinet").Output);

            // A stem two files share names neither; a stem one file has names it.
            var ambiguous = registry.Submit(line: "machine.swap cabinet gun");

            Assert.True(condition: ambiguous.IsError);
            Assert.Contains(expectedSubstring: "give the whole file name", actualString: ambiguous.Output);
            Assert.Contains(expectedSubstring: "status=Applied", actualString: registry.Submit(line: "machine.swap cabinet mirror").Output);
            Assert.Contains(expectedSubstring: "mounted=../carts/mirror.cgb.cartridge.json", actualString: registry.Submit(line: "machine.content cabinet").Output);

            // A name outside the folder is refused, and a path cannot reach past it.
            Assert.Contains(expectedSubstring: "names no file the folder offers", actualString: registry.Submit(line: "machine.swap cabinet ../world/x.cartridge.json").Output);
            Assert.True(condition: registry.Submit(line: "machine.content nobody").IsError);
            Assert.True(condition: registry.Submit(line: "machine.content").IsError);
            Assert.True(condition: registry.Submit(line: "machine.swap cabinet").IsError);
            Assert.True(condition: registry.Submit(line: "machine.swap nobody gun").IsError);
        }
    }
    // A reload submits the re-read document through the rebuild leaf, whose JSON holds no directory; the directory
    // rides beside it, so the document's machines still resolve their relative content beside the file.
    [Fact]
    public void AReloadedDocumentResolvesItsMachineContentBesideItsFile() {
        using var root = Layout();
        var (fixture, registry) = Boot(contentFolder: "../carts", root: root);

        using (fixture) {
            var reread = WithCabinet(contentFolder: "../carts");
            var cabinet = reread.Machines.Single(predicate: static machine => (machine.Name == "cabinet"));

            reread = (reread with {
                DocumentDirectory = WorldDocumentPaths.DirectoryOf(documentPath: Path.Combine(path1: root.PathOf(name: "world"), path2: "cabinet.world.json")),
                MachinesRaw = [.. reread.Machines.Where(predicate: static machine => (machine.Name != "cabinet")), cabinet with {
                    Configuration = JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.configuration.v1", model = "cgb", boot = "fast", content = new { path = "../carts/gun.cgb.cartridge.json" } }),
                }],
            });

            Assert.True(condition: WorldFrameCodec.TryEncode(
                failure: out var encodeFailure,
                frame: out var frame,
                payload: new WorldSubmissionPayload.Rebuild(Value: new WorldRebuildRequest(
                    ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: reread)),
                    Definition: reread,
                    Force: true,
                    Kind: WorldRebuildKind.Reload,
                    Origin: new WorldRebuildOrigin.File(Path: Path.Combine(path1: root.PathOf(name: "world"), path2: "cabinet.world.json"))
                ))
            ), userMessage: encodeFailure.ToString());
            Assert.True(condition: WorldFrameCodec.TryDecode(failure: out var decodeFailure, frame: frame, payload: out var decoded), userMessage: decodeFailure.ToString());

            var request = Assert.IsType<WorldSubmissionPayload.Rebuild>(@object: decoded).Value;

            Assert.Equal(expected: reread.DocumentDirectory, actual: request.Definition!.DocumentDirectory);
            fixture.Server.EnqueueRebuild(principal: Principal.Console, request: request);
            fixture.Step();

            Assert.Contains(expectedSubstring: "mounted=../carts/gun.cgb.cartridge.json", actualString: registry.Submit(line: "machine.content cabinet").Output);
        }
    }
    [Fact]
    public void AMachineWithNoFolderOrAMissingOneListsNothing() {
        using var root = Layout();
        var (bare, bareRegistry) = Boot(contentFolder: null, root: root);

        using (bare) {
            Assert.Contains(expectedSubstring: "names no contentFolder", actualString: bareRegistry.Submit(line: "machine.content cabinet").Output);
        }

        var (missing, missingRegistry) = Boot(contentFolder: "../absent", root: root);

        using (missing) {
            Assert.Contains(expectedSubstring: "is not a folder", actualString: missingRegistry.Submit(line: "machine.content cabinet").Output);
        }
    }
    [Fact]
    public void ARawContentEngineListsEveryFileAndADocumentWithoutADirectoryResolvesNoFolder() {
        var tunes = new WorldMachine(
            "jukebox",
            "tune-instrument",
            JsonSerializer.SerializeToElement(new { schema = "puck.tune-instrument.configuration.v1" }),
            ContentFolder: "tunes"
        );

        Assert.True(
            condition: WorldMachineContentFolder.TryList(
                catalog: TestMachines.Catalog(),
                documentDirectory: Shipped(relativePath: "worlds"),
                entries: out var entries,
                machine: tunes,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: Directory.EnumerateFiles(path: Shipped(relativePath: "worlds/tunes")).Select(selector: Path.GetFileName).Order(comparer: StringComparer.Ordinal),
            actual: entries.Select(selector: static entry => entry.Name)
        );
        Assert.All(collection: entries, action: static entry => Assert.StartsWith(expectedStartString: "tunes/", actualString: entry.ContentPath));
        Assert.False(condition: WorldMachineContentFolder.TryList(
            catalog: TestMachines.Catalog(),
            documentDirectory: null,
            entries: out _,
            machine: tunes,
            reason: out var unresolved
        ));
        Assert.Contains(expectedSubstring: "contentFolder", actualString: unresolved);
    }
    [Theory]
    [InlineData("/cartridges")]
    [InlineData("C:/cartridges")]
    [InlineData("../cartridges/")]
    [InlineData("./cartridges")]
    [InlineData("..\\cartridges")]
    public void AFolderSpelledOutsideThePortableRelativeFormIsRefusedByName(string folder) {
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: WithCabinet(contentFolder: folder),
            machines: TestMachines.Catalog(),
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(expectedSubstring: $"contentFolder '{folder}'", actualString: reason);
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: WithCabinet(contentFolder: "../cartridges/cgb"),
                machines: TestMachines.Catalog(),
                neighbours: null,
                reason: out var accepted
            ),
            userMessage: accepted
        );
    }
}
