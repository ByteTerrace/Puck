using Puck.Testing;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldAuthorityReceiptSnapshotLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorldAuthorityOperationReceipt Receipt(string actor, bool applied) => new(
        Guid.NewGuid(),
        actor,
        ("payload:" + actor),
        (applied
        ? "applied"
        : "refused"),
        applied,
        1,
        null
    );

    [Fact]
    public async Task CapturedRootSelectsExactReceiptHistoryDespiteLaterPublications() {
        using var directory = new TemporaryDirectory();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var store = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "amber")
        );
        var fence = await store.AcquireActivationAsync(
            identity,
            Token
        );
        var applied = Receipt(
            actor: "applied",
            applied: true
        );

        Assert.True(condition: (await store.WriteCheckpointAsync(
            identity,
            "checkpoint"u8.ToArray(),
            4,
            Token,
            fence,
            applied
        )).Ok);
        var refused = Receipt(
            actor: "refused",
            applied: false
        );

        Assert.True(condition: (await store.RecordReceiptAsync(
            identity,
            refused,
            Token,
            fence
        )).Ok);
        var selected = (await store.LoadRootAsync(
            identity,
            Token
        ))!.Value;
        var later = Receipt(
            actor: "later",
            applied: false
        );

        Assert.True(condition: (await store.RecordReceiptAsync(
            identity,
            later,
            Token,
            fence
        )).Ok);
        var latest = await store.LoadRootAsync(
            identity,
            Token
        );

        var snapshot = await new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        ).CaptureReceiptSnapshotAsync(
            identity,
            selected,
            Token
        );

        Assert.Equal(
            selected,
            snapshot.Source
        );
        Assert.Equal(
            identity.Owner,
            snapshot.Owner
        );
        Assert.Equal(
            identity.World.Value,
            snapshot.World
        );
        var receipts = snapshot.Validate();

        Assert.Equal(
            2,
            receipts.Count
        );
        Assert.Equal(
            applied,
            receipts[applied.OperationId]
        );
        Assert.Equal(
            refused,
            receipts[refused.OperationId]
        );
        Assert.False(condition: receipts.ContainsKey(key: later.OperationId));
        Assert.Equal(
            later,
            await store.FindOperationReceiptAsync(
                identity,
                later.OperationId,
                Token
            )
        );
        Assert.Equal(
            latest,
            await store.LoadRootAsync(
                identity,
                Token
            )
        );
        var indexAddress = new ObjectBlobAddress(
            identity.Owner,
            $"private/puck/hosted/amber/authority/receipt-index/{selected.Root.ReceiptIndexHash![10..]}.json"
        );

        Assert.Equal(
            (await blobs.ReadAsync(
                target,
                indexAddress,
                Token
            ))!.Value.Content.ToArray(),
            snapshot.Index
        );
        foreach (var node in snapshot.Nodes) {
            var address = new ObjectBlobAddress(
                identity.Owner,
                $"private/puck/hosted/amber/authority/receipts/{node.Key[10..]}.rcpt"
            );

            Assert.Equal(
                (await blobs.ReadAsync(
                    target,
                    address,
                    Token
                ))!.Value.Content.ToArray(),
                node.Value
            );
        }
        var duplicateIndex = System.Text.Encoding.UTF8.GetBytes(s: $"{{\"{applied.OperationId:D}\":\"{snapshot.Nodes.Keys.First()}\",\"{applied.OperationId:D}\":\"{snapshot.Nodes.Keys.Last()}\"}}");
        var duplicate = snapshot with {
            Index = duplicateIndex,
            Source = selected with {
                Root = selected.Root with {
                    ReceiptIndexHash = WorldDefinitionFileSource.ComputeContentHash(content: duplicateIndex),
                },
            },
        };

        Assert.Contains(
            "duplicate",
            Assert.Throws<InvalidDataException>(testCode: () => duplicate.Validate()).Message
        );

        // Retain valid content pins while breaking a chain edge. Checking index lookups alone would miss this.
        var originalHead = selected.Root.ReceiptHash!;
        var nodeJson = JsonNode.Parse(snapshot.Nodes[originalHead])!;

        nodeJson["previous"] = ("sha256-64/" + new string(
            c: 'f',
            count: 16
        ));
        var brokenNode = JsonSerializer.SerializeToUtf8Bytes(nodeJson);
        var brokenPin = WorldDefinitionFileSource.ComputeContentHash(content: brokenNode);
        var brokenNodes = snapshot.Nodes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
        );

        brokenNodes.Remove(key: originalHead);
        brokenNodes.Add(
            key: brokenPin,
            value: brokenNode
        );
        var indexJson = JsonNode.Parse(snapshot.Index)!;

        indexJson[refused.OperationId.ToString(format: "D")] = brokenPin;
        var brokenIndex = JsonSerializer.SerializeToUtf8Bytes(indexJson);
        var broken = snapshot with {
            Nodes = brokenNodes,
            Index = brokenIndex,
            Source = selected with {
                Root = selected.Root with {
                    ReceiptHash = brokenPin,
                    ReceiptIndexHash = WorldDefinitionFileSource.ComputeContentHash(content: brokenIndex),
                },
            },
        };

        Assert.Contains(
            "chain",
            Assert.Throws<InvalidDataException>(testCode: () => broken.Validate()).Message
        );
        var missing = snapshot with { Nodes = new Dictionary<string, byte[]>() };

        Assert.Throws<InvalidDataException>(testCode: () => missing.Validate());
        var corrupt = snapshot with { Index = "corrupt"u8.ToArray() };

        Assert.Throws<InvalidDataException>(testCode: () => corrupt.Validate());
        var nullNode = snapshot with {
            Nodes = snapshot.Nodes.ToDictionary(
            pair => pair.Key,
            _ => ((byte[])null!)
        ),
        };

        Assert.Throws<InvalidDataException>(testCode: () => nullNode.Validate());
    }
    [Fact]
    public async Task EmptyHistoryIsExplicitAndCorruptSourceObjectsRefuse() {
        using var directory = new TemporaryDirectory();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var store = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "amber")
        );
        var fence = await store.AcquireActivationAsync(
            identity,
            Token
        );
        var empty = (await store.LoadRootAsync(
            identity,
            Token
        ))!.Value;
        var emptySnapshot = await store.CaptureReceiptSnapshotAsync(
            identity,
            empty,
            Token
        );

        Assert.Empty(collection: emptySnapshot.Index);
        Assert.Empty(collection: emptySnapshot.Nodes);
        Assert.Empty(collection: emptySnapshot.Validate());
        Assert.Throws<InvalidDataException>(testCode: () => (emptySnapshot with { World = "../invalid" }).Validate());
        Assert.Throws<InvalidDataException>(testCode: () => (emptySnapshot with { Index = "{}"u8.ToArray() }).Validate());

        Assert.True(condition: (await store.RecordReceiptAsync(
            identity,
            Receipt(
                actor: "refused",
                applied: false
            ),
            Token,
            fence
        )).Ok);
        var selected = (await store.LoadRootAsync(
            identity,
            Token
        ))!.Value;
        var address = new ObjectBlobAddress(
            identity.Owner,
            $"private/puck/hosted/amber/authority/receipts/{selected.Root.ReceiptHash![10..]}.rcpt"
        );

        await blobs.WriteAsync(
            target,
            address,
            "corrupt"u8.ToArray(),
            ObjectBlobWriteMode.Overwrite,
            cancellationToken: Token
        );
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => store.CaptureReceiptSnapshotAsync(
            identity,
            selected,
            Token
        ));
        Assert.Equal(
            selected,
            await store.LoadRootAsync(
                identity,
                Token
            )
        );
    }
}
