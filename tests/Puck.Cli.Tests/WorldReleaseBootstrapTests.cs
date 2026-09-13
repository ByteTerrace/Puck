using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Puck.Cli.Azure;
using Puck.Storage;
using Puck.State;
using Puck.World;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldReleaseBootstrapTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapPublishesAnEmptyGroupButRefusesExistingGameplayBeforePublishingAnyWorld(bool gameplay) {
        var directory = Directory.CreateTempSubdirectory("puck-release-bootstrap-");
        try {
            var services = new ServiceCollection();
            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services);
            using var provider = services.BuildServiceProvider();
            var target = new DirectoryObjectStorageTarget(Path.Combine(directory.FullName, "store"));
            var blobs = provider.GetRequiredService<IObjectBlobStore>();
            var owner = Guid.NewGuid();
            var token = TestContext.Current.CancellationToken;
            var authority = new WorldAuthorityBlobStore(blobs, target);
            var archive = new WorldReleaseArchive(blobs, target, owner);
            var bytes = WorldDefinitionSerialization.Serialize(new WorldDefinition());
            var package = Path.Combine(directory.FullName, "package");
            Directory.CreateDirectory(package);
            File.WriteAllBytes(Path.Combine(package, "world.json"), bytes);
            var pin = "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            var amber = new WorldAuthorityIdentity(owner, SafeName.Parse("amber"));
            var plum = new WorldAuthorityIdentity(owner, SafeName.Parse("plum"));
            var manifest = new WorldReleaseManifest {
                Label = "bootstrap", SourceRevision = "test", EngineImageDigest = "sha256:" + new string('a', 64),
                PersistenceContract = "test", PeerProtocolContract = "test",
                Definitions = new Dictionary<string, string> { [$"{owner:D}/amber"] = pin, [$"{owner:D}/plum"] = pin },
                DefinitionFiles = new Dictionary<string, string> { [$"{owner:D}/amber"] = "world.json", [$"{owner:D}/plum"] = "world.json" },
            };
            await archive.SaveAsync(manifest, package, token);
            if (gameplay) {
                Assert.True((await authority.WriteCheckpointAsync(plum, "player-progress"u8.ToArray(), 12, token)).Ok);
                var before = await authority.LoadRootAsync(plum, token);
                await Assert.ThrowsAsync<InvalidDataException>(() => AzureCommand.InitializeWorldReleaseBootstrapAsync(manifest, archive, authority, owner, token));
                Assert.Null(await authority.LoadRootAsync(amber, token));
                Assert.Equal(before, await authority.LoadRootAsync(plum, token));
            } else {
                await AzureCommand.InitializeWorldReleaseBootstrapAsync(manifest, archive, authority, owner, token);
                await AzureCommand.InitializeWorldReleaseBootstrapAsync(manifest, archive, authority, owner, token);
                foreach (var identity in new[] { amber, plum }) {
                    var root = await authority.LoadRootAsync(identity, token);
                    Assert.NotNull(root);
                    Assert.Equal(Guid.Empty, root.Value.Root.FenceToken);
                    Assert.Null(root.Value.Root.CheckpointHash);
                    Assert.Equal(-1, root.Value.Root.JournalSequence);
                    Assert.Equal(bytes, WorldDefinitionSerialization.Serialize((await authority.LoadDefinitionAsync(identity, token))!));
                }
            }
        } finally { directory.Delete(recursive: true); }
    }
}
