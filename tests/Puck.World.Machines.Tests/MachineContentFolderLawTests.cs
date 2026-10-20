using System.Numerics;
using System.Text.Json;
using Puck.AdvancedGamingBrick;
using Puck.Commands;
using Puck.HumbleGamingBrick;
using Puck.Networking;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Machines.Tests;

/// <summary>
/// THE LAW: a machine's authored <c>contentFolders</c> are the whole of what it can swap to, and none of them has to
/// exist. <c>machine.content</c> lists every file directly in each folder, resolved beside the declaring document and
/// in authored order, each mountable or refused with the reason its engine gives, and names a folder absent rather
/// than refusing it; <c>machine.swap</c> mounts one mountable file by file name, folder-qualified name or unambiguous
/// stem, and refuses any other name by name. An engine mounts what its content provider prepares, or a raw image it
/// judges runnable by the image's header, whatever the file's extension. A machine whose authored content is absent
/// boots with no cartridge, and a folder that vanishes while the World runs leaves the mounted cartridge running. A
/// machine preferring a folder default boots the file its folders' own settings name, and its authored content when
/// none is named or the named file cannot boot.
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

    private const string Mirror = "../carts/mirror.cgb.cartridge.json";

    private static string Shipped(string relativePath) => RepositoryPaths.Resolve(relativePath: $"worlds/debug/{relativePath}");
    // A brick image of the given cartridge type whose header checksum holds.
    private static byte[] BrickImage(byte type) {
        var rom = new byte[0x8000];

        rom[0x0147] = type;
        rom[0x014D] = CartridgeHeader.ComputeHeaderChecksum(rom: rom);

        return rom;
    }
    // A document directory beside: carts, two compiled cartridges, a source the provider does not prepare, and a third
    // cartridge sharing the second's stem; local, a raw brick image, an image of a mapper the brick does not implement,
    // and a cartridge named as one in carts; junk, only files the brick cannot mount; empty, nothing. No missing folder
    // exists.
    private static TemporaryDirectory Layout() {
        var root = new TemporaryDirectory(
            bestEffortDelete: true,
            prefix: "puck-content-folder-"
        );
        var carts = root.PathOf(name: "carts");
        var local = root.PathOf(name: "local");
        var junk = root.PathOf(name: "junk");

        _ = Directory.CreateDirectory(path: root.PathOf(name: "world"));
        _ = Directory.CreateDirectory(path: carts);
        _ = Directory.CreateDirectory(path: local);
        _ = Directory.CreateDirectory(path: junk);
        _ = Directory.CreateDirectory(path: root.PathOf(name: "empty"));
        File.Copy(destFileName: Path.Combine(path1: carts, path2: "mirror.cgb.cartridge.json"), sourceFileName: Shipped(relativePath: "cartridges/cgb/hgb-mirror.cgb.cartridge.json"));
        File.Copy(destFileName: Path.Combine(path1: carts, path2: "gun.cgb.cartridge.json"), sourceFileName: Shipped(relativePath: "cartridges/cgb/light-gun.cgb.cartridge.json"));
        File.Copy(destFileName: Path.Combine(path1: carts, path2: "gun.dmg.cartridge.json"), sourceFileName: Shipped(relativePath: "cartridges/cgb/light-gun.cgb.cartridge.json"));
        File.Copy(destFileName: Path.Combine(path1: carts, path2: "mirror.cgb.puck"), sourceFileName: Shipped(relativePath: "cartridges/cgb/light-gun.cgb.puck"));
        File.WriteAllBytes(bytes: BrickImage(type: 0x00), path: Path.Combine(path1: local, path2: "plain.rom"));
        File.WriteAllBytes(bytes: BrickImage(type: 0x20), path: Path.Combine(path1: local, path2: "banked.rom"));
        File.Copy(destFileName: Path.Combine(path1: local, path2: "gun.cgb.cartridge.json"), sourceFileName: Shipped(relativePath: "cartridges/cgb/light-gun.cgb.cartridge.json"));
        File.WriteAllText(contents: "not a cartridge", path: Path.Combine(path1: junk, path2: "notes.txt"));
        File.WriteAllBytes(bytes: new byte[0x40], path: Path.Combine(path1: junk, path2: "short.rom"));

        return root;
    }
    private static WorldDefinition WithCabinet(IReadOnlyList<string>? contentFolders, string contentPath = Mirror, bool preferFolderDefault = false) {
        var document = Fixtures.BuildDocument();

        return document with {
            MachinesRaw = [.. document.Machines, new WorldMachine(
                "cabinet",
                "gaming-brick",
                JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.configuration.v1", model = "cgb", boot = "fast", content = new { path = contentPath } }),
                ContentFolders: contentFolders,
                PreferFolderDefault: preferFolderDefault
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
    private static (WorldFixture Fixture, CommandRegistry Registry) Boot(TemporaryDirectory root, IReadOnlyList<string>? contentFolders, string contentPath = Mirror,
        bool preferFolderDefault = false) {
        var fixture = Fixtures.FreshServer(
            definition: WithCabinet(contentFolders: contentFolders, contentPath: contentPath, preferFolderDefault: preferFolderDefault),
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

    // Re-applies the cabinet document through the reload leaf, as world.reload does, and runs the tick it lands in.
    private static void Reload(WorldFixture fixture, TemporaryDirectory root) {
        var path = Path.Combine(path1: root.PathOf(name: "world"), path2: "cabinet.world.json");
        var reread = (WithCabinet(contentFolders: ["../carts", "../local"], preferFolderDefault: true) with {
            DocumentDirectory = WorldDocumentPaths.DirectoryOf(documentPath: path),
        });

        fixture.Server.EnqueueRebuild(principal: Principal.Console, request: new WorldRebuildRequest(
            ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: reread)),
            Definition: reread,
            Force: true,
            Kind: WorldRebuildKind.Reload,
            Origin: new WorldRebuildOrigin.File(Path: path)
        ));
        fixture.Step();
    }
    private static string CapturedContent(WorldFixture fixture) => ContentOf(machines: fixture.Server.Machines.CaptureInstances());
    private static string DocumentContent(WorldFixture fixture) => ContentOf(machines: fixture.Server.Definition.Machines);
    private static string ContentOf(IReadOnlyList<WorldMachine> machines) => machines.Single(predicate: static machine => (machine.Name == "cabinet"))
        .Configuration.GetProperty(propertyName: "content").GetProperty(propertyName: "path").GetString()!;

    // A machine preferring a folder default boots the file its folders' settings name, at boot and at every reload,
    // while a capture of the session keeps its authored content; machine.default writes the settings, and with none set
    // the machine boots its authored content again.
    [Fact]
    public void AMachinePreferringAFolderDefaultBootsTheDefaultItsFolderNames() {
        using var root = Layout();

        WorldMachineContentFolder.WriteSettings(folder: root.PathOf(name: "local"), name: "plain.rom");

        var (fixture, registry) = Boot(contentFolders: ["../carts", "../local"], preferFolderDefault: true, root: root);

        using (fixture) {
            Assert.Equal(expected: "../local/plain.rom", actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);

            var listed = registry.Submit(line: "machine.content cabinet");

            Assert.Contains(expectedSubstring: "preferDefault=true default=../local/plain.rom |", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "files=3 default=plain.rom", actualString: listed.Output);
            Assert.DoesNotContain(expectedSubstring: WorldMachineContentFolder.SettingsFile, actualString: listed.Output);
            Assert.Equal(expected: Mirror, actual: CapturedContent(fixture: fixture));

            // A reset recreates the default it runs, and the capture still names the authored content.
            var generation = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation;

            var reset = registry.Submit(line: $"machine.operation cabinet {generation} machine.reset {{\"schema\":\"puck.gaming-brick.machine-reset.v1\"}}");

            Assert.False(condition: reset.IsError, userMessage: reset.Output);
            Assert.NotEqual(expected: generation, actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation);
            Assert.Equal(expected: "../local/plain.rom", actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);
            Assert.Equal(expected: Mirror, actual: CapturedContent(fixture: fixture));
            Assert.Equal(expected: Mirror, actual: DocumentContent(fixture: fixture));

            // A swap is captured and adopted by the document, and a re-preparation of the document keeps it; a reload
            // boots the default again, and a second reload keeps the running machine.
            Assert.Contains(expectedSubstring: "status=Applied", actualString: registry.Submit(line: "machine.swap cabinet ../carts/gun.cgb.cartridge.json").Output);
            Assert.Equal(expected: "../carts/gun.cgb.cartridge.json", actual: CapturedContent(fixture: fixture));
            Assert.Equal(expected: "../carts/gun.cgb.cartridge.json", actual: DocumentContent(fixture: fixture));

            var swapped = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation;

            Assert.True(condition: fixture.Server.Machines.TryPrepare(current: fixture.Server.Definition, candidate: fixture.Server.Definition, plan: out var plan, reason: out var reason), userMessage: reason);
            fixture.Server.Machines.Commit(plan: plan!);
            fixture.Server.Machines.Finish(plan: plan!);
            Assert.Equal(expected: swapped, actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation);
            Assert.Equal(expected: "../carts/gun.cgb.cartridge.json", actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);
            Reload(fixture: fixture, root: root);
            Assert.Equal(expected: "../local/plain.rom", actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);

            var reloaded = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation;

            Reload(fixture: fixture, root: root);
            Assert.Equal(expected: reloaded, actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation);

            // A swap onto the authored content itself still leaves the default to the next reload.
            Assert.Contains(expectedSubstring: "status=Applied", actualString: registry.Submit(line: "machine.swap cabinet mirror").Output);
            Assert.Equal(expected: Mirror, actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);
            Reload(fixture: fixture, root: root);
            Assert.Equal(expected: "../local/plain.rom", actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);

            // Setting a default in another folder names none in the first; none names none anywhere.
            var set = registry.Submit(line: "machine.default cabinet ../carts/gun.cgb.cartridge.json");

            Assert.False(condition: set.IsError, userMessage: set.Output);
            Assert.Contains(expectedSubstring: "default=../carts/gun.cgb.cartridge.json preferDefault=true", actualString: set.Output);
            Assert.DoesNotContain(expectedSubstring: "default", actualString: File.ReadAllText(path: Path.Combine(path1: root.PathOf(name: "local"), path2: WorldMachineContentFolder.SettingsFile)));
            Reload(fixture: fixture, root: root);
            Assert.Equal(expected: "../carts/gun.cgb.cartridge.json", actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);
            Assert.Contains(expectedSubstring: "default=none", actualString: registry.Submit(line: "machine.default cabinet none").Output);
            Reload(fixture: fixture, root: root);
            Assert.Equal(expected: Mirror, actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);
            Assert.Contains(expectedSubstring: "preferDefault=true default=none |", actualString: registry.Submit(line: "machine.content cabinet").Output);

            // The verb takes a mountable file the folders hold, from a local console or seat only.
            Assert.True(condition: registry.Submit(line: "machine.default cabinet").IsError);
            Assert.Contains(expectedSubstring: "cannot be mounted", actualString: registry.Submit(line: "machine.default cabinet banked.rom").Output);
            Assert.Contains(expectedSubstring: "no machine 'nobody'", actualString: registry.Submit(line: "machine.default nobody plain.rom").Output);
            Assert.Contains(
                expectedSubstring: "requires a local console or seat",
                actualString: registry.SubmitSession(line: "machine.default cabinet plain.rom", session: new TextCommandSource(registry: registry).CreateSession(principal: Principal.Peer(index: 0, generation: 0))).Output
            );

            // A folder whose settings cannot be written refuses by name.
            var localSettings = Path.Combine(path1: root.PathOf(name: "local"), path2: WorldMachineContentFolder.SettingsFile);

            File.Delete(path: localSettings);
            _ = Directory.CreateDirectory(path: localSettings);
            Assert.Contains(expectedSubstring: $"cannot write {WorldMachineContentFolder.SettingsFile}", actualString: registry.Submit(line: "machine.default cabinet plain.rom").Output);
        }
    }
    // A default the machine cannot boot leaves it on its authored content, refused by folder and never by the default's
    // own name; settings are read strictly.
    [Theory]
    [InlineData("{ \"schema\": \"puck.content-folder.v1\", \"default\": \"absent.rom\" }", "names a default the folder does not hold")]
    [InlineData("{ \"schema\": \"puck.content-folder.v1\", \"default\": \"banked.rom\" }", "names a default the engine cannot mount")]
    [InlineData("{ \"schema\": \"puck.content-folder.v1\", \"defualt\": \"plain.rom\" }", "has unknown member 'defualt'")]
    [InlineData("{ \"schema\": \"puck.content-folder.v2\", \"default\": \"plain.rom\" }", "must carry schema")]
    [InlineData("{ \"schema\": \"puck.content-folder.v1\", \"default\": 7 }", "member 'default' must be a string")]
    [InlineData("{ \"schema\": \"puck.content-folder.v1\", \"default\": \"../carts/x.rom\" }", "default must be the bare name of a file in the folder")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{ not json", "is not valid JSON")]
    [InlineData(null, "is unreadable")]
    public void AFolderDefaultTheMachineCannotBootLeavesItsAuthoredContent(string? settings, string refusal) {
        using var root = Layout();
        var path = Path.Combine(path1: root.PathOf(name: "local"), path2: WorldMachineContentFolder.SettingsFile);

        if (settings is null) {
            _ = Directory.CreateDirectory(path: path);
        } else {
            File.WriteAllText(contents: settings, path: path);
        }

        var (fixture, registry) = Boot(contentFolders: ["../carts", "../local"], preferFolderDefault: true, root: root);

        using (fixture) {
            Assert.Equal(expected: Mirror, actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);
            Assert.Contains(expectedSubstring: $"defaultRefused=\"folder '../local' {WorldMachineContentFolder.SettingsFile} {refusal}", actualString: registry.Submit(line: "machine.content cabinet").Output);
        }
    }
    [Fact]
    public void FolderSettingsNameABareFileOrNone() {
        using var root = Layout();
        var local = root.PathOf(name: "local");

        File.WriteAllText(contents: "{ \"schema\": \"puck.content-folder.v1\", \"default\": null }", path: Path.Combine(path1: local, path2: WorldMachineContentFolder.SettingsFile));
        Assert.True(condition: WorldMachineContentFolder.TryList(catalog: TestMachines.Catalog(), documentDirectory: root.PathOf(name: "world"), entries: out var entries, folders: out var folders, machine: WithCabinet(contentFolders: ["../local"]).Machines[^1], reason: out var reason), userMessage: reason);
        Assert.False(condition: WorldMachineContentFolder.TryDefault(entries: entries, entry: out _, folders: folders, refusal: out var refusal));
        Assert.Null(@object: refusal);
        File.WriteAllBytes(bytes: new byte[5000], path: Path.Combine(path1: local, path2: WorldMachineContentFolder.SettingsFile));
        Assert.True(condition: WorldMachineContentFolder.TryList(catalog: TestMachines.Catalog(), documentDirectory: root.PathOf(name: "world"), entries: out entries, folders: out folders, machine: WithCabinet(contentFolders: ["../local"]).Machines[^1], reason: out reason), userMessage: reason);
        Assert.Contains(expectedSubstring: "is larger than", actualString: Assert.Single(collection: folders).SettingsRefusal);
        Assert.Throws<ArgumentException>(testCode: () => WorldMachineContentFolder.WriteSettings(folder: local, name: WorldMachineContentFolder.SettingsFile));
        Assert.Throws<ArgumentException>(testCode: () => WorldMachineContentFolder.WriteSettings(folder: local, name: ".."));
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: WithCabinet(contentFolders: null, preferFolderDefault: true), machines: TestMachines.Catalog(), neighbours: null, reason: out var unfoldered));
        Assert.Contains(expectedSubstring: "preferFolderDefault needs contentFolders", actualString: unfoldered);
    }
    [Fact]
    public void TheFoldersListWhatTheEngineMountsAndASwapMountsItByName() {
        using var root = Layout();
        var (fixture, registry) = Boot(contentFolders: ["../carts", "../local", "../missing", "../empty", "../junk"], root: root);

        using (fixture) {
            var listed = registry.Submit(line: "machine.content cabinet");

            Assert.False(condition: listed.IsError, userMessage: listed.Output);
            Assert.Contains(expectedSubstring: $"mounted={Mirror} mountable=5 refused=4", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "| folder path=../carts", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "present=true files=4", actualString: listed.Output);
            // A folder that does not exist, and one that is empty, are listed, never refused.
            Assert.Contains(expectedSubstring: "| folder path=../missing", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "present=false files=0", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "present=true files=0", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "| mirror.cgb.cartridge.json folder=../carts mounted=true", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "| gun.cgb.cartridge.json folder=../carts mounted=false |", actualString: listed.Output);
            // A raw image is admitted by its header, whatever its extension; the rest are refused with the reason.
            Assert.Contains(expectedSubstring: "| plain.rom folder=../local mounted=false |", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "| banked.rom folder=../local mounted=false refused=\"cartridge type 0x20 names a mapper the machine does not implement\"", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "| mirror.cgb.puck folder=../carts mounted=false refused=\"no gaming-brick cartridge header", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "| short.rom folder=../junk mounted=false refused=\"the image is 64 bytes, shorter than its 0x150-byte header\"", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "| notes.txt folder=../junk mounted=false refused=", actualString: listed.Output);

            var raw = registry.Submit(line: "machine.swap cabinet plain.rom");

            Assert.False(condition: raw.IsError, userMessage: raw.Output);
            Assert.Contains(expectedSubstring: "status=Applied", actualString: raw.Output);
            Assert.Contains(expectedSubstring: "mounted=../local/plain.rom", actualString: registry.Submit(line: "machine.content cabinet").Output);

            // A file the engine cannot mount is refused by name with its reason, and the machine keeps running.
            var banked = registry.Submit(line: "machine.swap cabinet banked.rom");

            Assert.True(condition: banked.IsError);
            Assert.Contains(expectedSubstring: "'../local/banked.rom' cannot be mounted: cartridge type 0x20", actualString: banked.Output);
            Assert.Contains(expectedSubstring: "'../local/banked.rom' cannot be mounted", actualString: registry.Submit(line: "machine.swap cabinet banked").Output);
            Assert.Contains(expectedSubstring: "mounted=../local/plain.rom", actualString: registry.Submit(line: "machine.content cabinet").Output);

            // One name in two folders names neither; the folder-qualified name names one.
            var ambiguous = registry.Submit(line: "machine.swap cabinet gun.cgb.cartridge.json");

            Assert.True(condition: ambiguous.IsError);
            Assert.Contains(expectedSubstring: "../carts/gun.cgb.cartridge.json, ../local/gun.cgb.cartridge.json); give the folder and file name", actualString: ambiguous.Output);
            Assert.Contains(expectedSubstring: "status=Applied", actualString: registry.Submit(line: "machine.swap cabinet ../local/gun.cgb.cartridge.json").Output);
            Assert.Contains(expectedSubstring: "mounted=../local/gun.cgb.cartridge.json", actualString: registry.Submit(line: "machine.content cabinet").Output);

            // A stem two mountable files share names neither; a refused source sharing a stem does not count.
            Assert.Contains(expectedSubstring: "give the whole file name", actualString: registry.Submit(line: "machine.swap cabinet gun").Output);
            Assert.Contains(expectedSubstring: "status=Applied", actualString: registry.Submit(line: "machine.swap cabinet mirror").Output);
            Assert.Contains(expectedSubstring: $"mounted={Mirror}", actualString: registry.Submit(line: "machine.content cabinet").Output);

            // A name outside the folders is refused, and a path cannot reach past them.
            Assert.Contains(expectedSubstring: "names no file the folders hold", actualString: registry.Submit(line: "machine.swap cabinet ../world/x.cartridge.json").Output);
            Assert.True(condition: registry.Submit(line: "machine.content nobody").IsError);
            Assert.True(condition: registry.Submit(line: "machine.content").IsError);
            Assert.True(condition: registry.Submit(line: "machine.swap cabinet").IsError);
            Assert.True(condition: registry.Submit(line: "machine.swap nobody gun").IsError);
        }
    }
    // Every folder absent or empty, and the authored boot content absent too: the machine boots with no cartridge,
    // the world boots, and the listing says so.
    [Fact]
    public void AMachineWhoseContentAndFoldersAreAbsentBootsWithNoCartridge() {
        using var root = Layout();
        var (fixture, registry) = Boot(contentFolders: ["../missing", "../empty"], contentPath: "../missing/boot.cgb.cartridge.json", root: root);

        using (fixture) {
            fixture.Step();

            var listed = registry.Submit(line: "machine.content cabinet");

            Assert.False(condition: listed.IsError, userMessage: listed.Output);
            Assert.Contains(expectedSubstring: "mounted=\"no cartridge\" mountable=0 refused=0", actualString: listed.Output);
            Assert.Null(@object: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);
            Assert.Contains(expectedSubstring: "names no file the folders hold (none)", actualString: registry.Submit(line: "machine.swap cabinet boot").Output);

            // A reset recreates the machine it runs, with no cartridge, rather than reading the absent file.
            var booted = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value;
            var reset = registry.Submit(line: $"machine.operation cabinet {booted.Generation} machine.reset {{\"schema\":\"puck.gaming-brick.machine-reset.v1\"}}");

            Assert.False(condition: reset.IsError, userMessage: reset.Output);
            Assert.Contains(expectedSubstring: "status=Applied", actualString: reset.Output);

            var after = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value;

            Assert.NotEqual(expected: booted.Generation, actual: after.Generation);
            Assert.Null(@object: after.Content);
            Assert.Contains(expectedSubstring: "mounted=\"no cartridge\"", actualString: registry.Submit(line: "machine.content cabinet").Output);
            fixture.Step();
            Assert.Null(@object: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Fault);
        }
    }
    // A folder that empties or vanishes while the World runs: the next listing shows it, a swap no longer finds its
    // files, and the cartridge mounted from it keeps running.
    [Fact]
    public void AFolderThatVanishesWhileTheWorldRunsLeavesTheMountedCartridgeRunning() {
        using var root = Layout();
        var (fixture, registry) = Boot(contentFolders: ["../carts", "../local"], root: root);

        using (fixture) {
            Assert.Contains(expectedSubstring: "status=Applied", actualString: registry.Submit(line: "machine.swap cabinet plain.rom").Output);
            fixture.Step();
            foreach (var file in Directory.GetFiles(path: root.PathOf(name: "local"))) {
                File.Delete(path: file);
            }
            Assert.Contains(expectedSubstring: "| folder path=../local", actualString: registry.Submit(line: "machine.content cabinet").Output);
            Assert.Contains(expectedSubstring: "present=true files=0", actualString: registry.Submit(line: "machine.content cabinet").Output);
            Directory.Delete(path: root.PathOf(name: "local"));

            var listed = registry.Submit(line: "machine.content cabinet");

            Assert.False(condition: listed.IsError, userMessage: listed.Output);
            Assert.Contains(expectedSubstring: "mounted=../local/plain.rom", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "present=false files=0", actualString: listed.Output);
            Assert.Contains(expectedSubstring: "names no file the folders hold", actualString: registry.Submit(line: "machine.swap cabinet plain.rom").Output);

            var before = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value;

            fixture.Step();
            fixture.Step();

            var after = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value;

            Assert.Equal(expected: before.Generation, actual: after.Generation);
            Assert.Null(@object: after.Fault);
            Assert.Equal(expected: "../local/plain.rom", actual: after.Content);
        }
    }
    // A reload submits the re-read document through the rebuild leaf, whose JSON holds no directory; the directory
    // rides beside it, so the document's machines still resolve their relative content beside the file.
    [Fact]
    public void AReloadedDocumentResolvesItsMachineContentBesideItsFile() {
        using var root = Layout();
        var (fixture, registry) = Boot(contentFolders: ["../carts"], root: root);

        using (fixture) {
            var reread = WithCabinet(contentFolders: ["../carts"]);
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
    // Every mount, whichever verb or wire submission asks for it, names a file directly in one of the folders: a path
    // outside them, rooted or climbing out of one with '..', a file in a folder's subfolder, and a link in a folder that
    // leads out of it are refused by name, and the machine keeps running what it ran.
    [Fact]
    public void AContentInsertMountsOnlyAFileInTheFolders() {
        using var root = Layout();
        var outside = Path.Combine(path1: root.PathOf(name: "world"), path2: "outside.cgb.cartridge.json");
        var nested = Path.Combine(path1: root.PathOf(name: "carts"), path2: "nested");

        File.Copy(destFileName: outside, sourceFileName: Shipped(relativePath: "cartridges/cgb/light-gun.cgb.cartridge.json"));
        _ = Directory.CreateDirectory(path: nested);
        File.Copy(destFileName: Path.Combine(path1: nested, path2: "deep.cgb.cartridge.json"), sourceFileName: outside);

        var (fixture, registry) = Boot(contentFolders: ["../carts", "../local"], root: root);

        using (fixture) {
            string Insert(string path) => registry.Submit(line: $"machine.operation cabinet {fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation} content.insert {JsonSerializer.Serialize(new { schema = "puck.gaming-brick.content-insert.v1", content = new { path } })}").Output;

            foreach (var refused in new[] { outside, "../world/outside.cgb.cartridge.json", "../carts/../world/outside.cgb.cartridge.json", "../carts/nested/deep.cgb.cartridge.json" }) {
                Assert.Contains(expectedSubstring: "is not a file in machine 'cabinet' contentFolders (../carts, ../local)", actualString: Insert(path: refused));
            }
            Assert.Equal(expected: Mirror, actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);

            // A rooted spelling, or one that leaves a folder and comes back, of a file in a folder is the same file: it
            // is admitted, mounted under the folder's own spelling, and listed as mounted.
            Assert.Contains(expectedSubstring: "status=Applied", actualString: Insert(path: Path.Combine(path1: root.PathOf(name: "carts"), path2: "gun.cgb.cartridge.json")));
            Assert.Equal(expected: "../carts/gun.cgb.cartridge.json", actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);
            Assert.Contains(expectedSubstring: "| gun.cgb.cartridge.json folder=../carts mounted=true", actualString: registry.Submit(line: "machine.content cabinet").Output);
            Assert.Contains(expectedSubstring: "status=Applied", actualString: Insert(path: "../local/../carts/mirror.cgb.cartridge.json"));
            Assert.Equal(expected: Mirror, actual: fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Content);

            var link = Path.Combine(path1: root.PathOf(name: "carts"), path2: "link.cgb.cartridge.json");

            try {
                _ = File.CreateSymbolicLink(path: link, pathToTarget: outside);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Assert.Skip(reason: $"this machine cannot create a symbolic link: {exception.Message}");
            }
            Assert.Contains(expectedSubstring: "is not a file in machine 'cabinet' contentFolders", actualString: Insert(path: "../carts/link.cgb.cartridge.json"));
        }
    }
    [Fact]
    public void AMachineWithNoFoldersListsNothingAndMountsOnlyItsOwnContent() {
        using var root = Layout();
        var (bare, bareRegistry) = Boot(contentFolders: null, root: root);

        using (bare) {
            Assert.Contains(expectedSubstring: "names no contentFolders", actualString: bareRegistry.Submit(line: "machine.content cabinet").Output);

            var generation = bare.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation;

            Assert.Contains(expectedSubstring: "names no contentFolders, so '../carts/gun.cgb.cartridge.json' is not content it can mount", actualString: bareRegistry.Submit(line: $"machine.operation cabinet {generation} content.insert {{\"schema\":\"puck.gaming-brick.content-insert.v1\",\"content\":{{\"path\":\"../carts/gun.cgb.cartridge.json\"}}}}").Output);
            Assert.Contains(expectedSubstring: "status=Applied", actualString: bareRegistry.Submit(line: $"machine.operation cabinet {generation} content.insert {{\"schema\":\"puck.gaming-brick.content-insert.v1\",\"content\":{{\"path\":\"{Mirror}\"}}}}").Output);
        }
    }
    [Fact]
    public void ARawContentEngineListsEveryFileAndADocumentWithoutADirectoryResolvesNoFolder() {
        var tunes = new WorldMachine(
            "jukebox",
            "tune-instrument",
            JsonSerializer.SerializeToElement(new { schema = "puck.tune-instrument.configuration.v1" }),
            ContentFolders: ["tunes"]
        );

        Assert.True(
            condition: WorldMachineContentFolder.TryList(
                catalog: TestMachines.Catalog(),
                documentDirectory: RepositoryPaths.Resolve(relativePath: "worlds/debug"),
                entries: out var entries,
                folders: out var folders,
                machine: tunes,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: Directory.EnumerateFiles(path: Shipped(relativePath: "tunes")).Select(selector: Path.GetFileName).Order(comparer: StringComparer.Ordinal),
            actual: entries.Select(selector: static entry => entry.Name)
        );
        Assert.All(collection: entries, action: static entry => Assert.Null(@object: entry.Refusal));
        Assert.All(collection: entries, action: static entry => Assert.StartsWith(expectedStartString: "tunes/", actualString: entry.ContentPath));
        Assert.True(condition: Assert.Single(collection: folders).Present);
        Assert.False(condition: WorldMachineContentFolder.TryList(
            catalog: TestMachines.Catalog(),
            documentDirectory: null,
            entries: out _,
            folders: out _,
            machine: tunes,
            reason: out var unresolved
        ));
        Assert.Contains(expectedSubstring: "contentFolders", actualString: unresolved);
    }
    // Each engine judges a raw image by its header alone: its own format and a board it implements run, anything
    // else is refused with the reason, and an engine that judges no image refuses every one.
    [Fact]
    public void EachEngineJudgesARawImageByItsHeader() {
        var catalog = TestMachines.Catalog();
        var agb = new byte[AgbCartridgeHeader.Length];

        agb[0x0B2] = 0x96;
        agb[0x0BD] = AgbCartridgeHeader.ComputeComplementChecksum(rom: agb);

        byte[] Nes(byte flags6) => [(byte)'N', (byte)'E', (byte)'S', 0x1A, 1, 1, flags6, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(expected: CartridgeHeader.Length, actual: catalog.ImageHeaderLength(engineId: "gaming-brick"));
        Assert.Null(@object: catalog.RefuseImage(engineId: "gaming-brick", header: BrickImage(type: 0x1B).AsSpan(start: 0, length: CartridgeHeader.Length), imageLength: 0x8000));
        Assert.Null(@object: catalog.RefuseImage(engineId: "advanced-gaming-brick", header: agb, imageLength: 0x10000));
        Assert.Contains(expectedSubstring: "no advanced-gaming-brick cartridge header", actualString: catalog.RefuseImage(engineId: "advanced-gaming-brick", header: BrickImage(type: 0x00).AsSpan(start: 0, length: AgbCartridgeHeader.Length), imageLength: 0x8000));
        Assert.Null(@object: catalog.RefuseImage(engineId: "humble-gaming-deck", header: Nes(flags6: 0x00), imageLength: (16 + 16384 + 8192)));
        Assert.Contains(expectedSubstring: "Unimplemented mapper 2 (UxROM)", actualString: catalog.RefuseImage(engineId: "humble-gaming-deck", header: Nes(flags6: 0x20), imageLength: (16 + 16384 + 8192)));
        Assert.Contains(expectedSubstring: "Unimplemented MMC3 (mapper 4, submapper 0) memory layout", actualString: catalog.RefuseImage(engineId: "humble-gaming-deck", header: Nes(flags6: 0x40), imageLength: (16 + 16384 + 8192)));
        Assert.Contains(expectedSubstring: "Damaged NROM payload", actualString: catalog.RefuseImage(engineId: "humble-gaming-deck", header: Nes(flags6: 0x00), imageLength: 16));
        Assert.Contains(expectedSubstring: "no iNES or NES 2.0 header signature", actualString: catalog.RefuseImage(engineId: "humble-gaming-deck", header: agb.AsSpan(start: 0, length: 16), imageLength: 0x10000));

        var damaged = Nes(flags6: 0x00);

        damaged[12] = 1;
        Assert.Contains(expectedSubstring: "Ambiguous or damaged iNES header", actualString: catalog.RefuseImage(engineId: "humble-gaming-deck", header: damaged, imageLength: (16 + 16384 + 8192)));
        Assert.Equal(expected: 0, actual: catalog.ImageHeaderLength(engineId: "tune-instrument"));
        Assert.Contains(expectedSubstring: "judges no image by its header", actualString: catalog.RefuseImage(engineId: "tune-instrument", header: [], imageLength: 0));
    }
    // An unrunnable brick image is refused by name when a machine is created from it, never started.
    [Fact]
    public void ABrickRefusesAnImageItCannotRunByName() {
        var engine = new GamingBrickEngine();

        Assert.Contains(expectedSubstring: "cartridge type 0x20 names a mapper", actualString: Assert.Throws<ArgumentException>(testCode: () => engine.Create(options: "cgb fast", contentBytes: BrickImage(type: 0x20))).Message);
        Assert.Contains(expectedSubstring: "shorter than its 0x150-byte header", actualString: Assert.Throws<ArgumentException>(testCode: () => engine.Create(options: null, contentBytes: new byte[16])).Message);
    }
    [Theory]
    [InlineData("/cartridges")]
    [InlineData("C:/cartridges")]
    [InlineData("../cartridges/")]
    [InlineData("./cartridges")]
    [InlineData("..\\cartridges")]
    public void AFolderSpelledOutsideThePortableRelativeFormIsRefusedByName(string folder) {
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: WithCabinet(contentFolders: ["../cartridges/cgb", folder]),
            machines: TestMachines.Catalog(),
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(expectedSubstring: $"contentFolders[1] '{folder}'", actualString: reason);
    }
    // Validation reads only the spelling, never the disk: a folder absent on this machine is accepted, while an empty
    // list and a folder named twice are refused by name.
    [Fact]
    public void ValidationAcceptsAnAbsentFolderAndRefusesAnEmptyOrRepeatedList() {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: WithCabinet(contentFolders: ["../cartridges/cgb", "../../../../roms/never-present"]),
                machines: TestMachines.Catalog(),
                neighbours: null,
                reason: out var accepted
            ),
            userMessage: accepted
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: WithCabinet(contentFolders: []), machines: TestMachines.Catalog(), neighbours: null, reason: out var empty));
        Assert.Contains(expectedSubstring: "contentFolders names no folder", actualString: empty);
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: WithCabinet(contentFolders: ["../a", "../a"]), machines: TestMachines.Catalog(), neighbours: null, reason: out var twice));
        Assert.Contains(expectedSubstring: "contentFolders[1] '../a' is listed twice", actualString: twice);
    }
}
