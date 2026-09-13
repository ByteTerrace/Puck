using System.Security.Cryptography;
using System.Text.Json;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>Reads complete receipt facts and exercises the packaged store's lookup and duplicate handling.
/// The outer runner independently computes the expected input hash before starting each container.</summary>
internal static class WorldReleaseReceiptProof {
    public static async Task<SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>>> ReadAsync(
        WorldAuthorityBlobStore store, WorldSiloDefinition definition, bool allowMissingRoots, CancellationToken token) {
        var inventory = new SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>>(StringComparer.Ordinal);
        foreach (var row in definition.Worlds) {
            var identity = new WorldAuthorityIdentity(row.Owner, row.World);
            var root = await store.LoadRootAsync(identity, token).ConfigureAwait(false);
            var receipts = new SortedDictionary<Guid, WorldAuthorityOperationReceipt>();
            if (root is { } selected) {
                var snapshot = await store.CaptureReceiptSnapshotAsync(identity, selected, token).ConfigureAwait(false);
                foreach (var receipt in snapshot.Validate()) { receipts.Add(receipt.Key, receipt.Value); }
            } else if (!allowMissingRoots) { throw new InvalidDataException($"receipt qualification has no authority for '{row.World}'"); }
            inventory.Add($"{row.Owner:D}/{row.World}", receipts);
        }
        return inventory;
    }

    public static async Task<string> VerifyAsync(WorldAuthorityBlobStore store, WorldSiloDefinition definition,
        SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>> expected, bool testDuplicates, CancellationToken token) {
        var observed = new SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>>(StringComparer.Ordinal);
        foreach (var row in definition.Worlds) {
            var identity = new WorldAuthorityIdentity(row.Owner, row.World);
            var key = $"{row.Owner:D}/{row.World}";
            var receipts = new SortedDictionary<Guid, WorldAuthorityOperationReceipt>();
            var root = await store.LoadRootAsync(identity, token).ConfigureAwait(false) ?? throw new InvalidDataException("receipt qualification lost an authority root");
            foreach (var wanted in expected[key]) {
                var found = await store.FindOperationReceiptAsync(identity, wanted.Key, token).ConfigureAwait(false);
                if (found != wanted.Value) { throw new InvalidDataException($"receipt qualification lost or changed operation '{wanted.Key:D}' in '{row.World}'"); }
                receipts.Add(wanted.Key, found.Value);
                if (!testDuplicates) { continue; }
                if (root.Root.FenceToken != Guid.Empty) { throw new InvalidDataException("receipt duplicate checks require a drained disposable authority"); }
                var duplicate = await store.RecordReceiptAsync(identity, wanted.Value, token, WorldAuthorityFence.Unowned).ConfigureAwait(false);
                var conflict = await store.RecordReceiptAsync(identity, wanted.Value with { PayloadDigest = wanted.Value.PayloadDigest + "#qualification-conflict" }, token, WorldAuthorityFence.Unowned).ConfigureAwait(false);
                if (!duplicate.Ok || conflict.Kind != WorldAuthorityStoreOutcomeKind.OperationConflict || await store.LoadRootAsync(identity, token).ConfigureAwait(false) != root) {
                    throw new InvalidDataException($"receipt qualification changed authority or mishandled a retry for '{wanted.Key:D}' in '{row.World}'");
                }
            }
            observed.Add(key, receipts);
        }
        return Hash(observed);
    }

    public static string Hash(SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>> inventory) =>
        "sha256/" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inventory)));
}
