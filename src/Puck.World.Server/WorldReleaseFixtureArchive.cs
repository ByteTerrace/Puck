using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>One complete row captured at the host's shared simulation boundary.</summary>
public sealed record WorldReleaseFixtureCheckpoint(byte[] Encoded, ulong Tick, WorldAuthorityReceiptSnapshot Receipts);
/// <summary>A checkpoint object's exact identity, its captured row tick, and the exact pin of the receipt snapshot
/// captured with it.</summary>
public sealed record WorldReleaseFixtureRow(string Hash, ulong Tick, string ReceiptsHash);
/// <summary>Immutable inventory for a coherent qualification snapshot. It contains no production signing keys.</summary>
public sealed record WorldReleaseFixtureManifest(string Schema, Guid RequestId, string Group, string Release,
    Guid Owner, Guid MachineId, IReadOnlyDictionary<string, WorldReleaseFixtureRow> Worlds) {
    /// <summary>Host capture time for the coherent group, distinct from each world's simulation tick.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? CapturedAt { get; init; }
    /// <summary>Full digest of the canonical inventory, including every row's checkpoint hash.</summary>
    [JsonIgnore] public string Identity => ContentPin.Compute(content: WorldReleaseFixtureArchive.Canonicalize(manifest: this)).ToString();
    /// <summary>Enforced policy/inventory proof. Its absence means this fixture is not an intentional recovery point.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RewindBoundary { get; init; }
}
/// <summary>Publishes a complete fixture inventory only after its checkpoints and receipt snapshots are retained.
/// The host supplies all rows from one pump boundary; this archive never samples live authority roots.</summary>
public sealed class WorldReleaseFixtureArchive(IObjectBlobStore store, ObjectStorageTarget target, Guid owner) {
    private const long MaximumCaptureBytes = ((512L * 1024) * 1024);

    public const int MaximumCheckpointBytes = ((128 * 1024) * 1024);
    public const string Schema = "puck.world.release-fixture.v1";

    internal static byte[] Canonicalize(WorldReleaseFixtureManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest with {
        Worlds = new SortedDictionary<string, WorldReleaseFixtureRow>(
        manifest.Worlds.ToDictionary(
            row => row.Key,
            row => row.Value
        ),
        StringComparer.Ordinal
    ),
    });

    private ObjectBlobAddress CheckpointAddress(string hash) => new(
        owner,
        $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/fixtures/content/{Digest(pin: hash)}.pckp"
    );
    private static string Digest(string? pin) => (ContentPin.TryParse(
        pin: out var parsed,
        text: pin
    )
        ? parsed.Hex
        : throw new InvalidDataException(message: "release fixture requires full lowercase SHA-256 pins")
    );
    private ObjectBlobAddress ManifestAddress(Guid requestId) {
        if (
            (owner == Guid.Empty) ||
            (requestId == Guid.Empty)
        ) { throw new ArgumentException(message: "release fixture identities must not be empty"); }
        return new(
            Key: $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/fixtures/{requestId:D}.json",
            ObjectId: owner
        );
    }
    private ObjectBlobAddress ReceiptsAddress(string hash) => new(
        owner,
        $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/fixtures/content/{Digest(pin: hash)}.receipts"
    );
    private void Validate(WorldReleaseFixtureManifest manifest) {
        if (
            (owner == Guid.Empty) ||
            (manifest.Owner != owner) ||
            (manifest.Schema != Schema) ||
            (manifest.RequestId == Guid.Empty) ||
            (manifest.MachineId == Guid.Empty) ||
            (manifest.Worlds is null) ||
            (manifest.Worlds.Count == 0)
        ) {
            throw new InvalidDataException(message: "release fixture requires an owned complete inventory and stable identities");
        }
        _ = SafeName.Parse(candidate: manifest.Group);
        _ = Digest(pin: manifest.Release);
        if (manifest.RewindBoundary is { } boundary) {
            if (
                (boundary != WorldReleaseRewindBoundary.Compute(
                owner,
                manifest.Group,
                manifest.Worlds.Keys
            )) ||
                (manifest.CapturedAt is null)
            ) {
                throw new InvalidDataException(message: "recovery point has an incomplete capture or boundary proof");
            }
        } else if (manifest.CapturedAt is not null) { throw new InvalidDataException(message: "recovery time requires boundary proof"); }
        foreach (var row in manifest.Worlds) {
            _ = SafeName.Parse(candidate: row.Key); _ = Digest(pin: row.Value.Hash); _ = Digest(pin: row.Value.ReceiptsHash);
        }
    }
    private async Task WriteImmutableAsync(ObjectBlobAddress address, ReadOnlyMemory<byte> bytes, CancellationToken token) {
        var result = await store.WriteAsync(
            target,
            address,
            bytes,
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: token
        ).ConfigureAwait(continueOnCapturedContext: false);
        var retained = await store.ReadAsync(
            address: address,
            cancellationToken: token,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (retained is null) ||
            !retained.Value.Content.Span.SequenceEqual(other: bytes.Span)
        ) {
            throw new InvalidDataException(message: (result.Succeeded
                ? "release fixture upload failed readback"
                : "release fixture conflicts with retained bytes"));
        }
    }

    /// <summary>Reads the canonical inventory for a completed capture; partial uploads have no inventory.</summary>
    public async Task<WorldReleaseFixtureManifest?> LoadAsync(Guid requestId, CancellationToken cancellationToken = default) {
        var result = await store.ReadAsync(
            target,
            ManifestAddress(requestId: requestId),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (result is null) { return null; }
        if (result.Value.Content.Length > (1024 * 1024)) { throw new InvalidDataException(message: "release fixture inventory exceeds its byte budget"); }
        try {
            var manifest = (JsonSerializer.Deserialize<WorldReleaseFixtureManifest>(result.Value.Content.Span)
                ?? throw new InvalidDataException(message: "release fixture inventory is empty"));

            Validate(manifest: manifest);
            if (
                (manifest.RequestId != requestId) ||
                !result.Value.Content.Span.SequenceEqual(other: Canonicalize(manifest: manifest))
            ) {
                throw new InvalidDataException(message: "release fixture inventory is noncanonical or belongs to another request");
            }
            return manifest;
        } catch (Exception error) when ((error is JsonException or ArgumentException or NullReferenceException)) {
            throw new InvalidDataException(
                innerException: error,
                message: "release fixture inventory is malformed"
            );
        }
    }
    /// <summary>Reads and verifies one exact checkpoint from a retained inventory.</summary>
    public async Task<ReadOnlyMemory<byte>> ReadCheckpointAsync(WorldReleaseFixtureManifest manifest, string world, CancellationToken cancellationToken = default) {
        Validate(manifest: manifest);
        if (!manifest.Worlds.TryGetValue(
            key: world,
            value: out var row
        )) { throw new InvalidDataException(message: "world is absent from the release fixture"); }
        var result = (await store.ReadAsync(
            target,
            CheckpointAddress(hash: row.Hash),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidDataException(message: "retained release checkpoint is missing"));

        if (
            (result.Content.Length is 0 or > MaximumCheckpointBytes) ||
            (ContentPin.Compute(content: result.Content.Span).ToString() != row.Hash)
        ) {
            throw new InvalidDataException(message: "retained release checkpoint is corrupt or exceeds its byte budget");
        }
        return result.Content;
    }
    /// <summary>Reads the complete pinned receipt graph captured with one row.</summary>
    /// <param name="manifest">The retained fixture inventory.</param>
    /// <param name="world">The exact row to read.</param>
    /// <param name="cancellationToken">Cancels the storage read.</param>
    /// <returns>The validated receipt graph and captured root provenance.</returns>
    /// <exception cref="InvalidDataException">The inventory is malformed, or the receipt snapshot is missing, corrupt, oversized or names another world.</exception>
    public async Task<WorldAuthorityReceiptSnapshot> ReadReceiptsAsync(WorldReleaseFixtureManifest manifest, string world, CancellationToken cancellationToken = default) {
        Validate(manifest: manifest);
        if (!manifest.Worlds.TryGetValue(
            key: world,
            value: out var row
        )) { throw new InvalidDataException(message: "world is absent from the release fixture"); }
        var pin = row.ReceiptsHash;
        var content = (await store.ReadAsync(
            target,
            ReceiptsAddress(hash: pin),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidDataException(message: "retained release receipt snapshot is missing"));

        if (
            (content.Content.Length > WorldAuthorityReceiptSnapshot.MaximumEncodedBytes) ||
            (ContentPin.Compute(content: content.Content.Span).ToString() != pin)
        ) {
            throw new InvalidDataException(message: "retained release receipt snapshot is corrupt or oversized");
        }
        var snapshot = WorldAuthorityReceiptSnapshot.Decode(bytes: content.Content.Span);

        if (
            (snapshot.Owner != owner) ||
            (snapshot.World != world)
        ) { throw new InvalidDataException(message: "retained receipt snapshot belongs to another world"); }
        return snapshot;
    }
    /// <summary>Retains a detached capture under a stable request ID. A different capture cannot replace it.</summary>
    public async Task<WorldReleaseFixtureManifest> SaveAsync(Guid requestId, string group, string release, Guid machineId,
        IReadOnlyDictionary<string, WorldReleaseFixtureCheckpoint> checkpoints, CancellationToken cancellationToken = default,
        string? rewindBoundary = null, DateTimeOffset? capturedAt = null) {
        // Take ownership before the first await; a caller cannot change bytes behind their published hashes.
        var captured = new SortedDictionary<string, (byte[] Encoded, ulong Tick, byte[] Receipts)>(comparer: StringComparer.Ordinal);

        foreach (var row in checkpoints) {
            var history = (row.Value.Receipts ?? throw new InvalidDataException(message: "release fixture row has no receipt snapshot"));

            if (
                (rewindBoundary is not null) &&
                (history.Source.Root.RewindBoundary != rewindBoundary)
            ) {
                throw new InvalidDataException(message: "recovery point has no matching durable boundary proof");
            }
            if (
                (history.Owner != owner) ||
                (history.World != row.Key)
            ) { throw new InvalidDataException(message: "release fixture receipt snapshot belongs to another world"); }
            captured.Add(
                key: row.Key,
                value: (row.Value.Encoded.ToArray(), row.Value.Tick, history.Encode())
            );
        }
        if (captured.Values.Sum(selector: row => (((long)row.Encoded.Length) + row.Receipts.Length)) > MaximumCaptureBytes) { throw new InvalidDataException(message: "release fixture exceeds its capture budget"); }
        var rows = new SortedDictionary<string, WorldReleaseFixtureRow>(comparer: StringComparer.Ordinal);

        foreach (var row in captured) {
            if (row.Value.Encoded.Length is 0 or > MaximumCheckpointBytes) { throw new InvalidDataException(message: "release fixture checkpoint exceeds its byte budget"); }
            rows.Add(
                key: row.Key,
                value: new(
                    Hash: ContentPin.Compute(content: row.Value.Encoded).ToString(),
                    ReceiptsHash: ContentPin.Compute(content: row.Value.Receipts).ToString(),
                    Tick: row.Value.Tick
                )
            );
        }
        var manifest = new WorldReleaseFixtureManifest(
            Group: group,
            MachineId: machineId,
            Owner: owner,
            Release: release,
            RequestId: requestId,
            Schema: Schema,
            Worlds: rows
        ) {
            CapturedAt = capturedAt,
            RewindBoundary = rewindBoundary,
        };

        Validate(manifest: manifest);
        var bytes = Canonicalize(manifest: manifest);

        foreach (var row in captured) {
            await WriteImmutableAsync(
                address: CheckpointAddress(hash: rows[row.Key].Hash),
                bytes: row.Value.Encoded,
                token: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            await WriteImmutableAsync(
                address: ReceiptsAddress(hash: rows[row.Key].ReceiptsHash),
                bytes: row.Value.Receipts,
                token: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        await WriteImmutableAsync(
            address: ManifestAddress(requestId: requestId),
            bytes: bytes,
            token: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        return manifest;
    }
}
