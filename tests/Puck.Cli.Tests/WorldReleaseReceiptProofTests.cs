using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Cli.Automation;
using Puck.Storage;
using Puck.State;
using Puck.World;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldReleaseReceiptProofTests {
    [Fact]
    public async Task CompleteReceiptProofChecksLookupsAndRetriesWithoutChangingAuthority() {
        var temporary = Directory.CreateTempSubdirectory(prefix: "puck-receipt-proof-");

        try {
            var services = new ServiceCollection();

            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
            using var provider = services.BuildServiceProvider();
            var store = new WorldAuthorityBlobStore(
                store: provider.GetRequiredService<IObjectBlobStore>(),
                target: new DirectoryObjectStorageTarget(temporary.FullName)
            );
            var identity = new WorldAuthorityIdentity(
                Owner: Guid.NewGuid(),
                World: SafeName.Parse(candidate: "alpha")
            );
            var definition = new WorldSiloDefinition(
                [new(
                        identity.Owner,
                        identity.World,
                        new(KeyFile: "unused"),
                        Pinned: true
                    )],
                new(Budget: 1),
                new(
                    "directory",
                    JsonElement.Parse("{}")
                ),
                temporary.FullName,
                new(Kind: "Localhost")
            );
            var token = TestContext.Current.CancellationToken;

            await Assert.ThrowsAsync<InvalidDataException>(() => WorldReleaseReceiptProof.ReadAsync(
                store,
                definition,
                false,
                token
            ));
            var empty = await WorldReleaseReceiptProof.ReadAsync(
                store,
                definition,
                true,
                token
            );

            Assert.Empty(Assert.Single(empty).Value);
            var applied = new WorldAuthorityOperationReceipt(
                Guid.NewGuid(),
                "editor",
                "edit",
                "applied",
                true,
                1,
                null
            );

            Assert.True(condition: (await store.WriteCheckpointAsync(
                identity,
                "checkpoint"u8.ToArray(),
                1,
                token,
                receipt: applied
            )).Ok);
            var refused = new WorldAuthorityOperationReceipt(
                Guid.NewGuid(),
                "guest",
                "edit",
                "refused",
                false,
                2,
                null
            );

            Assert.True(condition: (await store.RecordReceiptAsync(
                identity,
                refused,
                token
            )).Ok);
            var expected = await WorldReleaseReceiptProof.ReadAsync(
                store,
                definition,
                false,
                token
            );

            Assert.Equal(
                2,
                Assert.Single(expected).Value.Count
            );
            var hash = WorldReleaseReceiptProof.Hash(expected);
            var root = await store.LoadRootAsync(
                cancellationToken: token,
                identity: identity
            );

            Assert.Equal(
                hash,
                await WorldReleaseReceiptProof.VerifyAsync(
                    store,
                    definition,
                    expected,
                    true,
                    token
                )
            );
            Assert.Equal(
                root,
                await store.LoadRootAsync(
                    cancellationToken: token,
                    identity: identity
                )
            );
            Assert.True(condition: (await store.RecordReceiptAsync(
                identity,
                refused with { OperationId = Guid.NewGuid(), PayloadDigest = "new" },
                token
            )).Ok);
            Assert.Equal(
                hash,
                await WorldReleaseReceiptProof.VerifyAsync(
                    store,
                    definition,
                    expected,
                    true,
                    token
                )
            );
            Assert.NotEqual(
                hash,
                WorldReleaseReceiptProof.Hash(await WorldReleaseReceiptProof.ReadAsync(
                    store,
                    definition,
                    false,
                    token
                ))
            );
            _ = await store.AcquireActivationAsync(
                cancellationToken: token,
                identity: identity
            );
            Assert.Equal(
                hash,
                await WorldReleaseReceiptProof.VerifyAsync(
                    store,
                    definition,
                    expected,
                    false,
                    token
                )
            );
            Assert.Contains(
                "drained",
                (await Assert.ThrowsAsync<InvalidDataException>(() => WorldReleaseReceiptProof.VerifyAsync(
                    store,
                    definition,
                    expected,
                    true,
                    token
                ))).Message
            );
            expected[Assert.Single(expected).Key][applied.OperationId] = applied with { DecisionCode = "changed" };
            Assert.Contains(
                "lost or changed",
                (await Assert.ThrowsAsync<InvalidDataException>(() => WorldReleaseReceiptProof.VerifyAsync(
                    store,
                    definition,
                    expected,
                    false,
                    token
                ))).Message
            );
        } finally { temporary.Delete(recursive: true); }
    }
}
