using System.Security.Cryptography;
using System.Text.Json;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>Reads complete receipt facts and exercises the packaged store's lookup and duplicate handling.
/// The outer runner independently computes the expected input hash before starting each container.</summary>
internal static class WorldReleaseReceiptProof {
    public static string Hash(SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>> inventory) =>
        ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: JsonSerializer.SerializeToUtf8Bytes(inventory))));
    public static async Task<SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>>> ReadAsync(
        WorldAuthorityBlobStore store, WorldSiloDefinition definition, bool allowMissingRoots, CancellationToken token) {
        var inventory = new SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>>(comparer: StringComparer.Ordinal);

        foreach (var row in definition.Worlds) {
            var identity = new WorldAuthorityIdentity(
                Owner: row.Owner,
                World: row.World
            );
            var root = await store.LoadRootAsync(
                cancellationToken: token,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);
            var receipts = new SortedDictionary<Guid, WorldAuthorityOperationReceipt>();

            if (root is { } selected) {
                var snapshot = await store.CaptureReceiptSnapshotAsync(
                    cancellationToken: token,
                    identity: identity,
                    source: selected
                ).ConfigureAwait(continueOnCapturedContext: false);

                foreach (var receipt in snapshot.Validate()) {
                    receipts.Add(
                        key: receipt.Key,
                        value: receipt.Value
                    );
                }
            } else if (!allowMissingRoots) { throw new InvalidDataException(message: $"receipt qualification has no authority for '{row.World}'"); }
            inventory.Add(
                key: $"{row.Owner:D}/{row.World}",
                value: receipts
            );
        }
        return inventory;
    }
    public static async Task<string> VerifyAsync(WorldAuthorityBlobStore store, WorldSiloDefinition definition,
        SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>> expected, bool testDuplicates, CancellationToken token) {
        var observed = new SortedDictionary<string, SortedDictionary<Guid, WorldAuthorityOperationReceipt>>(comparer: StringComparer.Ordinal);

        foreach (var row in definition.Worlds) {
            var identity = new WorldAuthorityIdentity(
                Owner: row.Owner,
                World: row.World
            );
            var key = $"{row.Owner:D}/{row.World}";
            var receipts = new SortedDictionary<Guid, WorldAuthorityOperationReceipt>();
            var root = (await store.LoadRootAsync(
                cancellationToken: token,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false) ?? throw new InvalidDataException(message: "receipt qualification lost an authority root"));

            foreach (var wanted in expected[key]) {
                var found = await store.FindOperationReceiptAsync(
                    identity,
                    wanted.Key,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (found != wanted.Value) { throw new InvalidDataException(message: $"receipt qualification lost or changed operation '{wanted.Key:D}' in '{row.World}'"); }
                receipts.Add(
                    key: wanted.Key,
                    value: found.Value
                );
                if (!testDuplicates) { continue; }
                if (root.Root.FenceToken != Guid.Empty) { throw new InvalidDataException(message: "receipt duplicate checks require a drained disposable authority"); }
                var duplicate = await store.RecordReceiptAsync(
                    identity,
                    wanted.Value,
                    token,
                    WorldAuthorityFence.Unowned
                ).ConfigureAwait(continueOnCapturedContext: false);
                var conflict = await store.RecordReceiptAsync(
                    identity,
                    wanted.Value with { PayloadDigest = (wanted.Value.PayloadDigest + "#qualification-conflict") },
                    token,
                    WorldAuthorityFence.Unowned
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (
                    !duplicate.Ok ||
                    (conflict.Kind != WorldAuthorityStoreOutcomeKind.OperationConflict) ||
                    (await store.LoadRootAsync(
                    cancellationToken: token,
                    identity: identity
                ).ConfigureAwait(continueOnCapturedContext: false) != root)
                ) {
                    throw new InvalidDataException(message: $"receipt qualification changed authority or mishandled a retry for '{wanted.Key:D}' in '{row.World}'");
                }
            }
            observed.Add(
                key: key,
                value: receipts
            );
        }
        return Hash(inventory: observed);
    }
}
