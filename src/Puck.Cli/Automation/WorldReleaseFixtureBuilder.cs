using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>Materializes a disposable, credential-free qualification store from a complete retained capture.
/// Bootstrap uses the same inventory without creating authority state; the marker is published last.</summary>
internal sealed class WorldReleaseFixtureBuilder(IObjectBlobStore blobs, WorldReleaseArchive releases, WorldReleaseFixtureArchive fixtures) {
    public async Task BuildAsync(WorldReleaseManifest release, Guid owner, WorldReleaseFixtureManifest? snapshot,
        string directory, CancellationToken token) {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) { throw new IOException("qualification export requires a fresh output directory"); }
        _ = CliWorldVocabulary.EnsureInstalled();
        var worlds = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in release.DefinitionFiles) {
            var parts = row.Key.Split('/');
            if (parts.Length != 2 || parts[0] != owner.ToString("D")) { throw new InvalidDataException("qualification release inventory belongs to another owner"); }
            _ = SafeName.Parse(parts[1]);
            worlds.Add(parts[1], row.Value);
        }
        if (worlds.Count == 0 || (snapshot is not null && (snapshot.Owner != owner || snapshot.Release != release.Identity ||
            !worlds.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.Worlds.Keys)))) {
            throw new InvalidDataException("qualification snapshot does not match the exact release inventory");
        }
        await releases.VerifyAsync(release, token).ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory, "store"), maximumBlobBytes: WorldReleaseFixtureArchive.MaximumCheckpointBytes);
        var authority = new WorldAuthorityBlobStore(blobs, target);
        var keys = Directory.CreateDirectory(Path.Combine(directory, "keys"));
        var state = Directory.CreateDirectory(Path.Combine(directory, "state"));
        await File.WriteAllTextAsync(Path.Combine(state.FullName, "silo-machine.id"), (snapshot?.MachineId ?? Guid.NewGuid()).ToString("D"), token).ConfigureAwait(false);
        var rows = new JsonArray();
        foreach (var world in worlds) {
            token.ThrowIfCancellationRequested();
            var identity = new WorldAuthorityIdentity(owner, SafeName.Parse(world.Key));
            var definitionBytes = await releases.ReadFileAsync(release, world.Value, token).ConfigureAwait(false);
            var definition = WorldDefinitionSerialization.Deserialize(definitionBytes.ToArray());
            if (snapshot is null) {
                var written = await blobs.WriteAsync(target, WorldOwnedWorldSync.HostedAddressFor(owner, identity.World, "definition.json"),
                    definitionBytes, ObjectBlobWriteMode.CreateOnly, cancellationToken: token).ConfigureAwait(false);
                if (!written.Succeeded) { throw new IOException("bootstrap fixture definition could not be published"); }
            } else {
                var checkpointBytes = await fixtures.ReadCheckpointAsync(snapshot, world.Key, token).ConfigureAwait(false);
                if (!WorldAuthorityCheckpointCodec.TryDecode(checkpointBytes.Span, out _, out var reason)) {
                    throw new InvalidDataException("qualification snapshot checkpoint is invalid: " + reason);
                }
                var published = await authority.PublishDefinitionAsync(identity, definition, token).ConfigureAwait(false);
                if (!published.Ok) { throw new IOException(published.Detail); }
                var checkpoint = await authority.WriteCheckpointAsync(identity, checkpointBytes, snapshot.Worlds[world.Key].Tick, token).ConfigureAwait(false);
                if (!checkpoint.Ok) { throw new IOException(checkpoint.Detail); }
            }
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var keyPath = Path.Combine(keys.FullName, $"{owner:D}-{world.Key}.pk8").Replace('\\', '/');
            await File.WriteAllBytesAsync(keyPath, key.ExportPkcs8PrivateKey(), token).ConfigureAwait(false);
            rows.Add(new JsonObject { ["owner"] = owner.ToString("D"), ["world"] = world.Key, ["pinned"] = true,
                ["federation"] = new JsonObject { ["keyFile"] = keyPath } });
        }
        var config = new JsonObject {
            ["schema"] = "puck.silo.configuration.v1", ["worlds"] = rows, ["doors"] = new JsonObject { ["budget"] = rows.Count },
            ["store"] = new JsonObject { ["type"] = "directory", ["settings"] = new JsonObject { ["path"] = Path.Combine(directory, "store").Replace('\\', '/') } },
            ["stateDir"] = state.FullName.Replace('\\', '/'), ["clustering"] = new JsonObject { ["kind"] = "Localhost" },
        };
        var path = Path.Combine(directory, "silo.json");
        CliFiles.WriteJson(path, config);
        if (!WorldSiloDefinitionSerialization.TryLoadFile(path, out _, out var refusal)) { throw new InvalidDataException(refusal); }
        CliFiles.WriteJson(Path.Combine(directory, "export.json"), new JsonObject {
            ["release"] = release.Identity, ["capture"] = snapshot?.Identity, ["requestId"] = snapshot?.RequestId.ToString("D"),
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "qualification.fixture"), "puck.world.qualification.v1", token).ConfigureAwait(false);
    }
}
