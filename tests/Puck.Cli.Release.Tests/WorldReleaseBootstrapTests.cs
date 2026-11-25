using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Puck.Cli.Automation;
using Puck.Storage;
using Puck.State;
using Puck.Testing;
using Puck.World;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Release.Tests;

public sealed class WorldReleaseBootstrapTests {
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Theory]
    public async Task BootstrapPublishesAnEmptyGroupButRefusesExistingGameplayBeforePublishingAnyWorld(bool gameplay, bool deferredDraw) {
        using var directory = new TemporaryDirectory(prefix: "puck-release-bootstrap-");

        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using var provider = services.BuildServiceProvider();
        var target = WorldAuthorityBlobStore.DirectoryTarget(rootPath: Path.Combine(
            path1: directory.RootPath,
            path2: "store"
        ));
        var blobs = provider.GetRequiredService<IObjectBlobStore>();
        var owner = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var authority = new WorldAuthorityBlobStore(
            store: blobs,
            target: target,
            documentDirectory: null
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
            path1: directory.RootPath,
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
            CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
            Label = "bootstrap",
            SourceRevision = "test",
            Engine = ("sha256/" + new string(
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

            await Assert.ThrowsAsync<InvalidDataException>(testCode: () => WorldReleaseBootstrap.PublishAsync(
                archive: archive,
                authority: authority,
                cancellationToken: token,
                documentDirectory: null,
                history: [],
                manifest: manifest,
                owner: owner
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
            await WorldReleaseBootstrap.PublishAsync(
                archive: archive,
                authority: authority,
                cancellationToken: token,
                documentDirectory: null,
                history: [],
                manifest: manifest,
                owner: owner
            );
            await WorldReleaseBootstrap.PublishAsync(
                archive: archive,
                authority: authority,
                cancellationToken: token,
                documentDirectory: null,
                history: [],
                manifest: manifest,
                owner: owner
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
    }
    // silo.publish reads its file through WorldFileOrigin.TryReadPublishable. A document with a boot draw site proves
    // the difference: undrawn, the silo publishes the bytes a release's bootstrap carries, and the bootstrap finds the
    // row unchanged; drawn, as silo.publish once published it, the row holds one instance's cells and the bootstrap
    // refuses it as a different world definition unless the group's own recovered bootstrap captured it.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ASiloPublishedDocumentMatchesItsReleaseBootstrapOnlyWhenPublishedUndrawn(bool drawn) {
        using var directory = new TemporaryDirectory(prefix: "puck-silo-publish-");

        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using var provider = services.BuildServiceProvider();
        var target = WorldAuthorityBlobStore.DirectoryTarget(rootPath: Path.Combine(
            path1: directory.RootPath,
            path2: "store"
        ));
        var blobs = provider.GetRequiredService<IObjectBlobStore>();
        var owner = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var authority = new WorldAuthorityBlobStore(
            store: blobs,
            target: target,
            documentDirectory: null
        );
        var archive = new WorldReleaseArchive(
            blobs,
            target,
            owner
        );
        var bytes = WorldReleaseOfficialPackageTests.DeferredDrawDefinition();
        var package = Path.Combine(
            path1: directory.RootPath,
            path2: "package"
        );
        var path = Path.Combine(
            path1: package,
            path2: "world.json"
        );

        Directory.CreateDirectory(path: package);
        File.WriteAllBytes(
            bytes: bytes,
            path: path
        );

        var origin = new WorldFileOrigin(resolvedPath: path);
        WorldDefinition? published;
        string reason;

        Assert.True(
            condition: (drawn
                ? origin.TryLoad(
                    definition: out published,
                    instanceIdentity: "amber",
                    reason: out reason
                )
                : origin.TryReadPublishable(
                    definition: out published,
                    reason: out reason
                )),
            userMessage: reason
        );
        Assert.Equal(
            actual: WorldDefinitionSerialization.Serialize(definition: published!).AsSpan().SequenceEqual(other: bytes),
            expected: !drawn
        );

        var amber = new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "amber")
        );

        Assert.True(condition: (await authority.PublishDefinitionAsync(
            amber,
            published!,
            token
        )).Ok);

        var manifest = new WorldReleaseManifest {
            CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
            Label = "silo-publish",
            SourceRevision = "test",
            Engine = ("sha256/" + new string(
            c: 'a',
            count: 64
        )),
            PersistenceContract = "test",
            PeerProtocolContract = "test",
            Definitions = new Dictionary<string, string> { [$"{owner:D}/amber"] = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))) },
            DefinitionFiles = new Dictionary<string, string> { [$"{owner:D}/amber"] = "world.json" },
        };

        await archive.SaveAsync(
            cancellationToken: token,
            manifest: manifest,
            packageDirectory: package
        );

        Task Bootstrap(IReadOnlyList<WorldReleaseGroupHistoryEntry> history) => WorldReleaseBootstrap.PublishAsync(
            archive: archive,
            authority: authority,
            cancellationToken: token,
            documentDirectory: null,
            history: history,
            manifest: manifest,
            owner: owner
        );

        if (drawn) {
            Assert.Contains(
                actualString: (await Assert.ThrowsAsync<InvalidDataException>(testCode: () => Bootstrap(history: []))).Message,
                expectedSubstring: "existing different world definition"
            );
            // The same root captured by an earlier bootstrap of the group that recovered is the group's own definition,
            // which a later bootstrap replaces with its release's bytes.
            var operation = Guid.NewGuid();
            var captured = (await authority.CaptureRecoveryRootAsync(
                amber,
                operation,
                token
            ))!.Value;

            await Bootstrap(history: [new WorldReleaseGroupHistoryEntry {
                OperationId = operation,
                RecoveryRoots = new Dictionary<string, string> { [amber.RootKey] = captured.Pin },
                Result = "recovered: test",
                TargetRelease = "earlier",
            }]);
            Assert.Equal(
                bytes,
                (await authority.LoadPublishedDefinitionBytesAsync(
                    cancellationToken: token,
                    identity: amber
                ))!.Value.ToArray()
            );
        } else {
            await Bootstrap(history: []);
            Assert.Equal(
                bytes,
                (await authority.LoadPublishedDefinitionBytesAsync(
                    cancellationToken: token,
                    identity: amber
                ))!.Value.ToArray()
            );
        }
    }
}
