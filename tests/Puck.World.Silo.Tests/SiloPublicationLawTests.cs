using System.Text.Json;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Launcher;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Silo.Tests;

public sealed class SiloPublicationLawTests {
    // Publication needs a mailbox turn. A handler must return while that mailbox is still unpumped, so the
    // command thread can answer its next line and reach the turn that actually publishes the definition.
    [Fact]
    public async Task PublicationReturnsBeforeItsMailboxTurnAndTheNextLineIsAnswered() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var source = new TextCommandSource(registry: new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(source: () => source, tagging: new SiloConsoleTagging(output: output));
        var identity = new WorldAuthorityIdentity(Owner: Guid.NewGuid(), World: SafeName.Parse(candidate: "row"));
        var store = new FakeObjectBlobStore();
        var host = new WorldSiloHost(
            definition: new(
                Worlds: [new(identity.Owner, identity.World, new(KeyFile: "unused.key"))],
                Doors: new(Budget: 1),
                Store: new("directory", JsonElement.Parse("{}")),
                StateDir: directory.RootPath,
                Clustering: new(Kind: "Localhost")
            ),
            blobStore: store,
            routing: routing,
            storageTarget: WorldAuthorityBlobStore.DirectoryTarget(rootPath: directory.RootPath)
        );
        using var instances = host.Instances;
        // Publication uses the host directly; no Orleans turn may be needed to accept or finish it.
        var registry = new CommandRegistry(modules: [new SiloCommandModule(host, grainFactory: null!, routing)]);
        var path = PuckPaths.Normalize(path: Path.Combine(path1: directory.RootPath, path2: "row.world.json"));

        File.WriteAllBytes(path: path, bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()));
        var command = Task.Run(function: () => registry.Submit(line: $"silo.publish row \"{path}\""));
        var requested = await command.WaitAsync(timeout: TimeSpan.FromSeconds(seconds: 10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: requested.IsError, userMessage: requested.Output);
        Assert.Contains(expectedSubstring: "requested from", actualString: requested.Output);
        Assert.Equal(expected: 0, actual: store.WriteCount);
        Assert.Contains(expectedSubstring: "[silo.status:", actualString: registry.Submit(line: "silo.status").Output);

        host.DrainActivationMailbox();

        Assert.True(condition: (store.WriteCount > 0));
    }
}
