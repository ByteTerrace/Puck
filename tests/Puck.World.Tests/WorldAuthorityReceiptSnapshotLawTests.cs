using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldAuthorityReceiptSnapshotLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CapturedRootSelectsExactReceiptHistoryDespiteLaterPublications() {
        using var directory = new TempWorldDirectory();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var store = new WorldAuthorityBlobStore(blobs, target);
        var identity = new WorldAuthorityIdentity(Guid.NewGuid(), SafeName.Parse("amber"));
        var fence = await store.AcquireActivationAsync(identity, Token);
        var applied = Receipt("applied", true);
        Assert.True((await store.WriteCheckpointAsync(identity, "checkpoint"u8.ToArray(), 4, Token, fence, applied)).Ok);
        var refused = Receipt("refused", false);
        Assert.True((await store.RecordReceiptAsync(identity, refused, Token, fence)).Ok);
        var selected = (await store.LoadRootAsync(identity, Token))!.Value;
        var later = Receipt("later", false);
        Assert.True((await store.RecordReceiptAsync(identity, later, Token, fence)).Ok);
        var latest = await store.LoadRootAsync(identity, Token);

        var snapshot = await new WorldAuthorityBlobStore(blobs, target).CaptureReceiptSnapshotAsync(identity, selected, Token);
        Assert.Equal(selected, snapshot.Source);
        Assert.Equal(identity.Owner, snapshot.Owner);
        Assert.Equal(identity.World.Value, snapshot.World);
        var receipts = snapshot.Validate();
        Assert.Equal(2, receipts.Count);
        Assert.Equal(applied, receipts[applied.OperationId]);
        Assert.Equal(refused, receipts[refused.OperationId]);
        Assert.False(receipts.ContainsKey(later.OperationId));
        Assert.Equal(later, await store.FindOperationReceiptAsync(identity, later.OperationId, Token));
        Assert.Equal(latest, await store.LoadRootAsync(identity, Token));
        var indexAddress = new ObjectBlobAddress(identity.Owner, $"private/puck/hosted/amber/authority/receipt-index/{selected.Root.ReceiptIndexHash![10..]}.json");
        Assert.Equal((await blobs.ReadAsync(target, indexAddress, Token))!.Value.Content.ToArray(), snapshot.Index);
        foreach (var node in snapshot.Nodes) {
            var address = new ObjectBlobAddress(identity.Owner, $"private/puck/hosted/amber/authority/receipts/{node.Key[10..]}.rcpt");
            Assert.Equal((await blobs.ReadAsync(target, address, Token))!.Value.Content.ToArray(), node.Value);
        }
        var duplicateIndex = System.Text.Encoding.UTF8.GetBytes($"{{\"{applied.OperationId:D}\":\"{snapshot.Nodes.Keys.First()}\",\"{applied.OperationId:D}\":\"{snapshot.Nodes.Keys.Last()}\"}}");
        var duplicate = snapshot with { Index = duplicateIndex, Source = selected with { Root = selected.Root with {
            ReceiptIndexHash = WorldDefinitionFileSource.ComputeContentHash(duplicateIndex) } } };
        Assert.Contains("duplicate", Assert.Throws<InvalidDataException>(() => duplicate.Validate()).Message);

        // Retain valid content pins while breaking a chain edge. Checking index lookups alone would miss this.
        var originalHead = selected.Root.ReceiptHash!;
        var nodeJson = JsonNode.Parse(snapshot.Nodes[originalHead])!;
        nodeJson["previous"] = "sha256-64/" + new string('f', 16);
        var brokenNode = JsonSerializer.SerializeToUtf8Bytes(nodeJson);
        var brokenPin = WorldDefinitionFileSource.ComputeContentHash(brokenNode);
        var brokenNodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
        brokenNodes.Remove(originalHead);
        brokenNodes.Add(brokenPin, brokenNode);
        var indexJson = JsonNode.Parse(snapshot.Index)!;
        indexJson[refused.OperationId.ToString("D")] = brokenPin;
        var brokenIndex = JsonSerializer.SerializeToUtf8Bytes(indexJson);
        var broken = snapshot with { Nodes = brokenNodes, Index = brokenIndex, Source = selected with { Root = selected.Root with {
            ReceiptHash = brokenPin, ReceiptIndexHash = WorldDefinitionFileSource.ComputeContentHash(brokenIndex) } } };
        Assert.Contains("chain", Assert.Throws<InvalidDataException>(() => broken.Validate()).Message);
        var missing = snapshot with { Nodes = new Dictionary<string, byte[]>() };
        Assert.Throws<InvalidDataException>(() => missing.Validate());
        var corrupt = snapshot with { Index = "corrupt"u8.ToArray() };
        Assert.Throws<InvalidDataException>(() => corrupt.Validate());
        var nullNode = snapshot with { Nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, _ => (byte[])null!) };
        Assert.Throws<InvalidDataException>(() => nullNode.Validate());
    }

    [Fact]
    public async Task EmptyHistoryIsExplicitAndCorruptSourceObjectsRefuse() {
        using var directory = new TempWorldDirectory();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var store = new WorldAuthorityBlobStore(blobs, target);
        var identity = new WorldAuthorityIdentity(Guid.NewGuid(), SafeName.Parse("amber"));
        var fence = await store.AcquireActivationAsync(identity, Token);
        var empty = (await store.LoadRootAsync(identity, Token))!.Value;
        var emptySnapshot = await store.CaptureReceiptSnapshotAsync(identity, empty, Token);
        Assert.Empty(emptySnapshot.Index);
        Assert.Empty(emptySnapshot.Nodes);
        Assert.Empty(emptySnapshot.Validate());
        Assert.Throws<InvalidDataException>(() => (emptySnapshot with { World = "../invalid" }).Validate());
        Assert.Throws<InvalidDataException>(() => (emptySnapshot with { Index = "{}"u8.ToArray() }).Validate());

        Assert.True((await store.RecordReceiptAsync(identity, Receipt("refused", false), Token, fence)).Ok);
        var selected = (await store.LoadRootAsync(identity, Token))!.Value;
        var address = new ObjectBlobAddress(identity.Owner, $"private/puck/hosted/amber/authority/receipts/{selected.Root.ReceiptHash![10..]}.rcpt");
        await blobs.WriteAsync(target, address, "corrupt"u8.ToArray(), ObjectBlobWriteMode.Overwrite, cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CaptureReceiptSnapshotAsync(identity, selected, Token));
        Assert.Equal(selected, await store.LoadRootAsync(identity, Token));
    }

    private static WorldAuthorityOperationReceipt Receipt(string actor, bool applied) => new(Guid.NewGuid(), actor, "payload:" + actor,
        applied ? "applied" : "refused", applied, 1, null);
}
