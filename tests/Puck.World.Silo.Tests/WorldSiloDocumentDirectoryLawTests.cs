using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Launcher;
using Puck.Storage;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Silo.Tests;

/// <summary>
/// A hosted world's relative machine content resolves beside the silo document's <c>documentDirectory</c>, the layout
/// the silo image ships its composed documents in: the first activation and the recovery from its checkpoint both
/// prepare the machine from it. Without one, a hosted document has no directory, and its relative content is refused
/// by name rather than read from wherever the process runs.
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
            new DirectoryObjectStorageTarget(stateDirectory),
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
        File.Copy(
            destFileName: Path.Combine(path1: cartridges, path2: Path.GetFileName(path: ContentPath)),
            sourceFileName: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/cartridges/hgb-mirror.cgb.cartridge.json")
        );
        File.WriteAllBytes(keyFile, key.ExportPkcs8PrivateKey());

        var identity = new WorldAuthorityIdentity(Owner: Guid.NewGuid(), World: SafeName.Parse(candidate: "arcade"));
        var row = new WorldSiloWorldRow(identity.Owner, identity.World, new(KeyFile: keyFile));
        var store = PuckStorageTestComposition.BuildStore();
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: new DirectoryObjectStorageTarget(directory.RootPath),
            timeProvider: new VirtualClock()
        );
        var published = await backend.PublishDefinitionAsync(identity, WithRelativeCabinet(), TestContext.Current.CancellationToken);

        Assert.True(condition: published.Ok, userMessage: published.Detail);

        // The first activation prepares the cabinet from the published document, and the drain checkpoints it.
        var first = Host(directory.RootPath, store, output, row, worlds);

        using (first.Instances) {
            var activation = first.ActivateAsync(identity, TestContext.Current.CancellationToken);

            await PumpAsync(host: first, operation: activation);
            Assert.True(condition: await activation, userMessage: output.ToString());
            Assert.True(condition: first.Instances.TryGet(identity.World.Value, out var live));
            Assert.Equal(expected: PuckPaths.Normalize(path: worlds), actual: PuckPaths.Normalize(path: live!.Server.Definition.DocumentDirectory!));
            Assert.NotNull(@object: live.Server.Machines.InstanceState(name: "cabinet"));
            await PumpAsync(host: first, operation: first.DrainAsync(ct: TestContext.Current.CancellationToken));
        }

        // A replacement recovers the row from that checkpoint and prepares the cabinet beside the same directory.
        var recovered = Host(directory.RootPath, store, output, row, worlds);

        using (recovered.Instances) {
            var activation = recovered.ActivateAsync(identity, TestContext.Current.CancellationToken);

            await PumpAsync(host: recovered, operation: activation);
            Assert.True(condition: await activation, userMessage: output.ToString());
            Assert.True(condition: recovered.Instances.TryGet(identity.World.Value, out var live));
            Assert.NotNull(@object: live!.Server.Machines.InstanceState(name: "cabinet"));
            await PumpAsync(host: recovered, operation: recovered.DrainAsync(ct: TestContext.Current.CancellationToken));
        }

        // A silo document that names no directory gives the hosted document none, and the relative content is
        // refused by name.
        var directoryless = Host(directory.RootPath, store, output, row, documentDirectory: null);

        using (directoryless.Instances) {
            Exception? refusal = null;

            try {
                var activation = directoryless.ActivateAsync(identity, TestContext.Current.CancellationToken);

                await PumpAsync(host: directoryless, operation: activation);
                Assert.False(condition: await activation);
            } catch (ArgumentException exception) {
                refusal = exception;
            }

            Assert.NotNull(@object: refusal);
            Assert.Contains(expectedSubstring: "has no directory to resolve it beside", actualString: refusal!.Message);
            Assert.Contains(expectedSubstring: ContentPath, actualString: refusal.Message);
        }
    }
}
