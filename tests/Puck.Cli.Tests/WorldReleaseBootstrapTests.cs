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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Theory]
    public async Task BootstrapPublishesAnEmptyGroupButRefusesExistingGameplayBeforePublishingAnyWorld(bool gameplay, bool deferredDraw) {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-release-bootstrap-");

        try {
            var services = new ServiceCollection();

            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
            using var provider = services.BuildServiceProvider();
            var target = new DirectoryObjectStorageTarget(Path.Combine(
                path1: directory.FullName,
                path2: "store"
            ));
            var blobs = provider.GetRequiredService<IObjectBlobStore>();
            var owner = Guid.NewGuid();
            var token = TestContext.Current.CancellationToken;
            var authority = new WorldAuthorityBlobStore(
                store: blobs,
                target: target
            );
            var archive = new WorldReleaseArchive(
                blobs,
                target,
                owner
            );
            var bytes = (deferredDraw
                ? WorldReleaseOfficialPackageTests.DeferredDrawDefinition()
                : WorldDefinitionSerialization.Serialize(definition: new WorldDefinition())
            );
            var package = Path.Combine(
                path1: directory.FullName,
                path2: "package"
            );

            Directory.CreateDirectory(path: package);
            File.WriteAllBytes(
                Path.Combine(
                    path1: package,
                    path2: "world.json"
                ),
                bytes
            );
            var pin = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)));
            var amber = new WorldAuthorityIdentity(
                Owner: owner,
                World: SafeName.Parse(candidate: "amber")
            );
            var plum = new WorldAuthorityIdentity(
                Owner: owner,
                World: SafeName.Parse(candidate: "plum")
            );
            var manifest = new WorldReleaseManifest {
                Label = "bootstrap",
                SourceRevision = "test",
                EngineImageDigest = ("sha256:" + new string(
                c: 'a',
                count: 64
            )),
                PersistenceContract = "test",
                PeerProtocolContract = "test",
                Definitions = new Dictionary<string, string> { [$"{owner:D}/amber"] = pin, [$"{owner:D}/plum"] = pin },
                DefinitionFiles = new Dictionary<string, string> { [$"{owner:D}/amber"] = "world.json", [$"{owner:D}/plum"] = "world.json" },
            };

            await archive.SaveAsync(
                cancellationToken: token,
                manifest: manifest,
                packageDirectory: package
            );
            if (gameplay) {
                Assert.True(condition: (await authority.WriteCheckpointAsync(
                    plum,
                    "player-progress"u8.ToArray(),
                    12,
                    token
                )).Ok);
                var before = await authority.LoadRootAsync(
                    cancellationToken: token,
                    identity: plum
                );

                await Assert.ThrowsAsync<InvalidDataException>(() => AzureCommand.InitializeWorldReleaseBootstrapAsync(
                    manifest,
                    archive,
                    authority,
                    owner,
                    token
                ));
                Assert.Null(value: await authority.LoadRootAsync(
                    cancellationToken: token,
                    identity: amber
                ));
                Assert.Equal(
                    before,
                    await authority.LoadRootAsync(
                        cancellationToken: token,
                        identity: plum
                    )
                );
            } else {
                await AzureCommand.InitializeWorldReleaseBootstrapAsync(
                    manifest,
                    archive,
                    authority,
                    owner,
                    token
                );
                await AzureCommand.InitializeWorldReleaseBootstrapAsync(
                    manifest,
                    archive,
                    authority,
                    owner,
                    token
                );
                foreach (var identity in new[] { amber, plum }) {
                    var root = await authority.LoadRootAsync(
                        cancellationToken: token,
                        identity: identity
                    );

                    Assert.NotNull(value: root);
                    Assert.Equal(
                        Guid.Empty,
                        root.Value.Root.FenceToken
                    );
                    Assert.Null(@object: root.Value.Root.CheckpointHash);
                    Assert.Equal(
                        -1,
                        root.Value.Root.JournalSequence
                    );
                    Assert.Equal(
                        bytes,
                        (await authority.LoadPublishedDefinitionBytesAsync(
                            cancellationToken: token,
                            identity: identity
                        ))!.Value.ToArray()
                    );
                }
            }
        } finally { directory.Delete(recursive: true); }
    }
}
