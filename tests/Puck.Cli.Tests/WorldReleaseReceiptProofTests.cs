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

            await Assert.ThrowsAsync<InvalidDataException>(testCode: () => WorldReleaseReceiptProof.ReadAsync(
                allowMissingRoots: false,
                definition: definition,
                store: store,
                token: token
            ));
            var empty = await WorldReleaseReceiptProof.ReadAsync(
                allowMissingRoots: true,
                definition: definition,
                store: store,
                token: token
            );

            Assert.Empty(collection: Assert.Single(collection: empty).Value);
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
                token,
                WorldAuthorityFence.Unowned
            )).Ok);
            var expected = await WorldReleaseReceiptProof.ReadAsync(
                allowMissingRoots: false,
                definition: definition,
                store: store,
                token: token
            );

            Assert.Equal(
                2,
                Assert.Single(collection: expected).Value.Count
            );
            var hash = WorldReleaseReceiptProof.Hash(inventory: expected);
            var root = await store.LoadRootAsync(
                cancellationToken: token,
                identity: identity
            );

            Assert.Equal(
                hash,
                await WorldReleaseReceiptProof.VerifyAsync(
                    definition: definition,
                    expected: expected,
                    store: store,
                    testDuplicates: true,
                    token: token
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
                token,
                WorldAuthorityFence.Unowned
            )).Ok);
            Assert.Equal(
                hash,
                await WorldReleaseReceiptProof.VerifyAsync(
                    definition: definition,
                    expected: expected,
                    store: store,
                    testDuplicates: true,
                    token: token
                )
            );
            Assert.NotEqual(
                hash,
                WorldReleaseReceiptProof.Hash(inventory: await WorldReleaseReceiptProof.ReadAsync(
                    allowMissingRoots: false,
                    definition: definition,
                    store: store,
                    token: token
                ))
            );
            _ = await store.AcquireActivationAsync(
                cancellationToken: token,
                identity: identity
            );
            Assert.Equal(
                hash,
                await WorldReleaseReceiptProof.VerifyAsync(
                    definition: definition,
                    expected: expected,
                    store: store,
                    testDuplicates: false,
                    token: token
                )
            );
            Assert.Contains(
                "drained",
                (await Assert.ThrowsAsync<InvalidDataException>(testCode: () => WorldReleaseReceiptProof.VerifyAsync(
                    definition: definition,
                    expected: expected,
                    store: store,
                    testDuplicates: true,
                    token: token
                ))).Message
            );
            expected[Assert.Single(collection: expected).Key][applied.OperationId] = applied with { DecisionCode = "changed" };
            Assert.Contains(
                "lost or changed",
                (await Assert.ThrowsAsync<InvalidDataException>(testCode: () => WorldReleaseReceiptProof.VerifyAsync(
                    definition: definition,
                    expected: expected,
                    store: store,
                    testDuplicates: false,
                    token: token
                ))).Message
            );
        } finally { temporary.Delete(recursive: true); }
    }
}
