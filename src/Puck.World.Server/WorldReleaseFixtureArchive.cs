using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>One complete row captured at the host's shared simulation boundary.</summary>
public sealed record WorldReleaseFixtureCheckpoint(byte[] Encoded, ulong Tick);

/// <summary>A checkpoint object's exact identity and captured row tick.</summary>
public sealed record WorldReleaseFixtureRow(string Hash, ulong Tick);

/// <summary>Immutable inventory for a coherent qualification snapshot. It contains no production signing keys.</summary>
public sealed record WorldReleaseFixtureManifest(string Schema, Guid RequestId, string Group, string Release,
    Guid Owner, Guid MachineId, IReadOnlyDictionary<string, WorldReleaseFixtureRow> Worlds) {
    /// <summary>Full digest of the canonical inventory, including every row's checkpoint hash.</summary>
    [JsonIgnore] public string Identity => WorldReleaseFixtureArchive.Hash(WorldReleaseFixtureArchive.Canonicalize(this));
}

/// <summary>Publishes a complete fixture inventory only after its captured checkpoints are retained.
/// The host supplies all rows from one pump boundary; this archive never samples live authority roots.</summary>
public sealed class WorldReleaseFixtureArchive(IObjectBlobStore store, ObjectStorageTarget target, Guid owner) {
    public const string Schema = "puck.world.release-fixture.v1";
    public const int MaximumCheckpointBytes = 128 * 1024 * 1024;
    private const long MaximumCaptureBytes = 512L * 1024 * 1024;

    /// <summary>Retains a detached capture under a stable request ID. A different capture cannot replace it.</summary>
    public async Task<WorldReleaseFixtureManifest> SaveAsync(Guid requestId, string group, string release, Guid machineId,
        IReadOnlyDictionary<string, WorldReleaseFixtureCheckpoint> checkpoints, CancellationToken cancellationToken = default) {
        // Take ownership before the first await; a caller cannot change bytes behind their published hashes.
        var captured = new SortedDictionary<string, WorldReleaseFixtureCheckpoint>(StringComparer.Ordinal);
        foreach (var row in checkpoints) { captured.Add(row.Key, new(row.Value.Encoded.ToArray(), row.Value.Tick)); }
        if (captured.Values.Sum(row => (long)row.Encoded.Length) > MaximumCaptureBytes) { throw new InvalidDataException("release fixture exceeds its capture budget"); }
        var rows = new SortedDictionary<string, WorldReleaseFixtureRow>(StringComparer.Ordinal);
        foreach (var row in captured) {
            if (row.Value.Encoded.Length is 0 or > MaximumCheckpointBytes) { throw new InvalidDataException("release fixture checkpoint exceeds its byte budget"); }
            rows.Add(row.Key, new(Hash(row.Value.Encoded), row.Value.Tick));
        }
        var manifest = new WorldReleaseFixtureManifest(Schema, requestId, group, release, owner, machineId, rows);
        Validate(manifest);
        var bytes = Canonicalize(manifest);
        foreach (var row in captured) {
            await WriteImmutableAsync(CheckpointAddress(rows[row.Key].Hash), row.Value.Encoded, cancellationToken).ConfigureAwait(false);
        }
        await WriteImmutableAsync(ManifestAddress(requestId), bytes, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>Reads the canonical inventory for a completed capture; partial uploads have no inventory.</summary>
    public async Task<WorldReleaseFixtureManifest?> LoadAsync(Guid requestId, CancellationToken cancellationToken = default) {
        var result = await store.ReadAsync(target, ManifestAddress(requestId), cancellationToken).ConfigureAwait(false);
        if (result is null) { return null; }
        if (result.Value.Content.Length > 1024 * 1024) { throw new InvalidDataException("release fixture inventory exceeds its byte budget"); }
        try {
            var manifest = JsonSerializer.Deserialize<WorldReleaseFixtureManifest>(result.Value.Content.Span)
                ?? throw new InvalidDataException("release fixture inventory is empty");
            Validate(manifest);
            if (manifest.RequestId != requestId || !result.Value.Content.Span.SequenceEqual(Canonicalize(manifest))) {
                throw new InvalidDataException("release fixture inventory is noncanonical or belongs to another request");
            }
            return manifest;
        } catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException) {
            throw new InvalidDataException("release fixture inventory is malformed", error);
        }
    }

    /// <summary>Reads and verifies one exact checkpoint from a retained inventory.</summary>
    public async Task<ReadOnlyMemory<byte>> ReadCheckpointAsync(WorldReleaseFixtureManifest manifest, string world, CancellationToken cancellationToken = default) {
        Validate(manifest);
        if (!manifest.Worlds.TryGetValue(world, out var row)) { throw new InvalidDataException("world is absent from the release fixture"); }
        var result = await store.ReadAsync(target, CheckpointAddress(row.Hash), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("retained release checkpoint is missing");
        if (result.Content.Length is 0 or > MaximumCheckpointBytes || Hash(result.Content.Span) != row.Hash) {
            throw new InvalidDataException("retained release checkpoint is corrupt or exceeds its byte budget");
        }
        return result.Content;
    }

    private void Validate(WorldReleaseFixtureManifest manifest) {
        if (owner == Guid.Empty || manifest.Owner != owner || manifest.Schema != Schema || manifest.RequestId == Guid.Empty ||
            manifest.MachineId == Guid.Empty || manifest.Worlds is null || manifest.Worlds.Count == 0) {
            throw new InvalidDataException("release fixture requires an owned complete inventory and stable identities");
        }
        _ = SafeName.Parse(manifest.Group);
        _ = Digest(manifest.Release);
        foreach (var row in manifest.Worlds) { _ = SafeName.Parse(row.Key); _ = Digest(row.Value.Hash); }
    }

    private async Task WriteImmutableAsync(ObjectBlobAddress address, ReadOnlyMemory<byte> bytes, CancellationToken token) {
        var result = await store.WriteAsync(target, address, bytes, ObjectBlobWriteMode.CreateOnly, cancellationToken: token).ConfigureAwait(false);
        var retained = await store.ReadAsync(target, address, token).ConfigureAwait(false);
        if (retained is null || !retained.Value.Content.Span.SequenceEqual(bytes.Span)) {
            throw new InvalidDataException(result.Succeeded ? "release fixture upload failed readback" : "release fixture conflicts with retained bytes");
        }
    }

    private ObjectBlobAddress ManifestAddress(Guid requestId) {
        if (owner == Guid.Empty || requestId == Guid.Empty) { throw new ArgumentException("release fixture identities must not be empty"); }
        return new(owner, $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/fixtures/{requestId:D}.json");
    }
    private ObjectBlobAddress CheckpointAddress(string hash) => new(owner, $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/fixtures/content/{Digest(hash)}.pckp");
    private static string Digest(string pin) => pin is { Length: 71 } && pin.StartsWith("sha256/", StringComparison.Ordinal) && pin.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0
        ? pin[7..] : throw new InvalidDataException("release fixture requires full lowercase SHA-256 pins");
    internal static string Hash(ReadOnlySpan<byte> bytes) => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] Canonicalize(WorldReleaseFixtureManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest with {
        Worlds = new SortedDictionary<string, WorldReleaseFixtureRow>(manifest.Worlds.ToDictionary(row => row.Key, row => row.Value), StringComparer.Ordinal),
    });
}
