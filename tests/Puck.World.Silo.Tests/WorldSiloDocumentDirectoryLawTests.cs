using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Launcher;
using Puck.Storage;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Silo.Tests;

/// <summary>
/// A hosted world's relative machine content resolves beside the silo document's <c>documentDirectory</c>, the layout
/// a host release ships its composed documents in: the first activation and the recovery from its checkpoint both
/// prepare the machine from it. Without one, a hosted document has no directory, and its machine is refused, naming
/// the content, rather than reading it from wherever the process runs.
/// </summary>
public sealed class WorldSiloDocumentDirectoryLawTests {
    // The image's layout: the composed documents under worlds/, and the authored cartridges beside that directory, so
    // a prepared document names its content "../cartridges/<file>".
    private const string ContentPath = "../cartridges/hgb-mirror.cgb.cartridge.json";

    private static WorldSiloHost Host(string stateDirectory, IObjectBlobStore store, BufferedConsoleOutput output, WorldSiloWorldRow row, string? documentDirectory) {
        var source = new TextCommandSource(new CommandRegistry(modules: []));

        return new(
            new(
                Clustering: new(Kind: "Localhost"),
                DocumentDirectory: documentDirectory,
                Doors: new(Budget: 1),
                StateDir: stateDirectory,
                Store: new("directory", JsonElement.Parse("{}")),
                Worlds: [row]
            ),
            store,
            new(
                source: () => source,
                tagging: new SiloConsoleTagging(output: output)
            ),
            WorldAuthorityBlobStore.DirectoryTarget(rootPath: stateDirectory),
            machineCatalog: TestMachines.Catalog(),
            timeProvider: new VirtualClock()
        );
    }
    private static WorldDefinition WithRelativeCabinet() {
        var document = Fixtures.BuildDocument();

        return document with {
            HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Presentation = WorldHostPresentation.None },
            MachinesRaw = [.. document.Machines, new WorldMachine(
                "cabinet",
                "gaming-brick",
                JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.configuration.v1", model = "cgb", boot = "fast", content = new { path = ContentPath } })
            )],
            ScreensRaw = [.. document.Screens, new WorldScreen(
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
            )],
        };
    }
    private static Task PumpAsync(WorldSiloHost host, Task operation) => WorldSiloHost.PumpActivationMailboxesAsync(
        cancellationToken: TestContext.Current.CancellationToken,
        hosts: [host],
        operation: operation
    );

    [Fact]
    public async Task RelativeMachineContentResolvesBesideTheSilosDocumentDirectoryOnActivationAndRecovery() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var worlds = Path.Combine(path1: directory.RootPath, path2: "image", path3: "worlds");
        var cartridges = Path.Combine(path1: directory.RootPath, path2: "image", path3: "cartridges");
        var keyFile = Path.Combine(path1: directory.RootPath, path2: "world.key");

        _ = Directory.CreateDirectory(path: worlds);
        _ = Directory.CreateDirectory(path: cartridges);
        // The authored source is the truth and its compiled twin is never committed, so the image's cartridge is
        // compiled from the source here.
        File.WriteAllBytes(
            bytes: Puck.GamingBricks.Forge.CartridgeDocuments.Canonicalize(document: Puck.GamingBricks.Transpiler.CartridgeSource.Read(sourcePath: RepositoryPaths.Resolve(relativePath: "worlds/debug/cartridges/cgb/hgb-mirror.cgb.puck"))).Bytes,
            path: Path.Combine(path1: cartridges, path2: Path.GetFileName(path: ContentPath))
        );
        File.WriteAllBytes(keyFile, key.ExportPkcs8PrivateKey());

        var identity = new WorldAuthorityIdentity(Owner: Guid.NewGuid(), World: SafeName.Parse(candidate: "arcade"));
        var row = new WorldSiloWorldRow(identity.Owner, identity.World, new(KeyFile: keyFile));
        var store = PuckStorageTestComposition.BuildStore();
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: WorldAuthorityBlobStore.DirectoryTarget(rootPath: directory.RootPath),
            timeProvider: new VirtualClock(),
            documentDirectory: null
        );
        var published = await backend.PublishDefinitionAsync(identity, WithRelativeCabinet(), TestContext.Current.CancellationToken);

        Assert.True(condition: published.Ok, userMessage: published.Detail);

        // A silo document that names no directory gives the hosted document none. The world still activates, and its
        // cabinet alone is a refused instance whose fault names the relative content, which is never read from
        // wherever the process runs. Its release checkpoints that refusal, and the next activation, which can prepare
        // the cabinet, recovers from it with the cabinet at its power-on image instead of refusing the world.
        var directoryless = Host(directory.RootPath, store, output, row, documentDirectory: null);

        using (directoryless.Instances) {
            var activation = directoryless.ActivateAsync(identity, TestContext.Current.CancellationToken);

            await PumpAsync(host: directoryless, operation: activation);
            Assert.True(condition: await activation, userMessage: output.ToString());
            Assert.True(condition: directoryless.Instances.TryGet(identity.World.Value, out var live));
            Assert.Null(@object: live!.Server.Definition.DocumentDirectory);

            var cabinet = live.Server.Machines.InstanceState(name: "cabinet");

            Assert.NotNull(@object: cabinet);
            Assert.Equal(expected: MachineRuntimeStatus.Refused, actual: cabinet.Value.Status);
            Assert.Null(@object: cabinet.Value.Content);
            Assert.Contains(expectedSubstring: "has no directory to resolve it beside", actualString: cabinet.Value.Fault);
            Assert.Contains(expectedSubstring: ContentPath, actualString: cabinet.Value.Fault);
        }

        // The next activation recovers that checkpoint beside the directory and prepares the cabinet, and the drain
        // checkpoints it running.
        var first = Host(directory.RootPath, store, output, row, worlds);

        using (first.Instances) {
            var activation = first.ActivateAsync(identity, TestContext.Current.CancellationToken);

            await PumpAsync(host: first, operation: activation);
            Assert.True(condition: await activation, userMessage: output.ToString());
            Assert.True(condition: first.Instances.TryGet(identity.World.Value, out var live));
            Assert.Equal(expected: PuckPaths.Normalize(path: worlds), actual: PuckPaths.Normalize(path: live!.Server.Definition.DocumentDirectory!));
            Assert.Equal(expected: ContentPath, actual: live.Server.Machines.InstanceState(name: "cabinet")?.Content);
            await PumpAsync(host: first, operation: first.DrainAsync(ct: TestContext.Current.CancellationToken));
        }

        // A replacement recovers the row from that checkpoint and prepares the cabinet beside the same directory.
        var recovered = Host(directory.RootPath, store, output, row, worlds);

        using (recovered.Instances) {
            var activation = recovered.ActivateAsync(identity, TestContext.Current.CancellationToken);

            await PumpAsync(host: recovered, operation: activation);
            Assert.True(condition: await activation, userMessage: output.ToString());
            Assert.True(condition: recovered.Instances.TryGet(identity.World.Value, out var live));
            Assert.Equal(expected: ContentPath, actual: live!.Server.Machines.InstanceState(name: "cabinet")?.Content);
            await PumpAsync(host: recovered, operation: recovered.DrainAsync(ct: TestContext.Current.CancellationToken));
        }

        // A directoryless silo never restores that checkpoint: its cabinet would be refused, and the running image the
        // checkpoint holds is never discarded, so the recovery is refused by name before any state is restored.
        var directorylessRecovery = Host(directory.RootPath, store, output, row, documentDirectory: null);

        using (directorylessRecovery.Instances) {
            var refusal = await Assert.ThrowsAsync<InvalidDataException>(testCode: async () => {
                var activation = directorylessRecovery.ActivateAsync(identity, TestContext.Current.CancellationToken);

                await PumpAsync(host: directorylessRecovery, operation: activation);
                _ = await activation;
            });

            Assert.Contains(expectedSubstring: "records a Running machine where this host's is Refused", actualString: refusal.Message);
            Assert.Contains(expectedSubstring: ContentPath, actualString: refusal.Message);
        }
    }
}
