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
        directory = Path.GetFullPath(path: directory);
        if (
            Directory.Exists(path: directory) ||
            File.Exists(path: directory)
        ) { throw new IOException(message: "qualification export requires a fresh output directory"); }
        _ = CliWorldVocabulary.EnsureInstalled();
        var worlds = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var row in release.DefinitionFiles) {
            var parts = row.Key.Split('/');

            if (
                (parts.Length != 2) ||
                (parts[0] != owner.ToString(format: "D"))
            ) { throw new InvalidDataException(message: "qualification release inventory belongs to another owner"); }
            _ = SafeName.Parse(candidate: parts[1]);
            worlds.Add(
                key: parts[1],
                value: row.Value
            );
        }
        if (
            (worlds.Count == 0) ||
            ((snapshot is not null) && ((snapshot.Owner != owner) || (snapshot.Release != release.Identity) ||
            !worlds.Keys.ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: snapshot.Worlds.Keys)))
        ) {
            throw new InvalidDataException(message: "qualification snapshot does not match the exact release inventory");
        }
        await releases.VerifyAsync(
            cancellationToken: token,
            manifest: release
        ).ConfigureAwait(continueOnCapturedContext: false);
        Directory.CreateDirectory(path: directory);
        var target = new DirectoryObjectStorageTarget(
            Path.Combine(
                path1: directory,
                path2: "store"
            ),
            maximumBlobBytes: WorldReleaseFixtureArchive.MaximumCheckpointBytes
        );
        var authority = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var keys = Directory.CreateDirectory(path: Path.Combine(
            path1: directory,
            path2: "keys"
        ));
        var state = Directory.CreateDirectory(path: Path.Combine(
            path1: directory,
            path2: "state"
        ));

        await File.WriteAllTextAsync(
            Path.Combine(
                path1: state.FullName,
                path2: "silo-machine.id"
            ),
            (snapshot?.MachineId ?? Guid.NewGuid()).ToString(format: "D"),
            token
        ).ConfigureAwait(continueOnCapturedContext: false);
        var rows = new JsonArray();

        foreach (var world in worlds) {
            token.ThrowIfCancellationRequested();
            var identity = new WorldAuthorityIdentity(
                Owner: owner,
                World: SafeName.Parse(candidate: world.Key)
            );
            var definitionBytes = await releases.ReadFileAsync(
                release,
                world.Value,
                token
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (snapshot is null) {
                var written = await blobs.WriteAsync(
                    target,
                    WorldOwnedWorldSync.HostedAddressFor(
                        owner,
                        identity.World,
                        "definition.json"
                    ),
                    definitionBytes,
                    ObjectBlobWriteMode.CreateOnly,
                    cancellationToken: token
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!written.Succeeded) { throw new IOException(message: "bootstrap fixture definition could not be published"); }
            } else {
                var checkpointBytes = await fixtures.ReadCheckpointAsync(
                    snapshot,
                    world.Key,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!WorldAuthorityCheckpointCodec.TryDecode(
                    bytes: checkpointBytes.Span,
                    checkpoint: out var captured,
                    reason: out var reason
                )) {
                    throw new InvalidDataException(message: ("qualification snapshot checkpoint is invalid: " + reason));
                }
                if (captured!.Server.LastCompletedTick != snapshot.Worlds[world.Key].Tick) { throw new InvalidDataException(message: "qualification checkpoint tick differs from its inventory"); }
                var receipts = await fixtures.ReadReceiptsAsync(
                    snapshot,
                    world.Key,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);
                var published = await authority.CreateReleaseFixtureAsync(
                    cancellationToken: token,
                    checkpoint: checkpointBytes,
                    definition: definitionBytes,
                    history: receipts,
                    identity: identity
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!published.Ok) { throw new IOException(message: published.Detail); }
            }
            using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
            var keyPath = Path.Combine(
                path1: keys.FullName,
                path2: $"{owner:D}-{world.Key}.pk8"
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );

            await File.WriteAllBytesAsync(
                keyPath,
                key.ExportPkcs8PrivateKey(),
                token
            ).ConfigureAwait(continueOnCapturedContext: false);
            rows.Add(value: new JsonObject {
                ["owner"] = owner.ToString(format: "D"),
                ["world"] = world.Key,
                ["pinned"] = true,
                ["federation"] = new JsonObject { ["keyFile"] = keyPath },
            });
        }
        var config = new JsonObject {
            ["schema"] = "puck.silo.configuration.v1",
            ["worlds"] = rows,
            ["doors"] = new JsonObject { ["budget"] = rows.Count },
            ["store"] = new JsonObject {
                ["type"] = "directory",
                ["settings"] = new JsonObject {
                    ["path"] = Path.Combine(
            path1: directory,
            path2: "store"
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        ),
                },
            },
            ["stateDir"] = state.FullName.Replace(
            newChar: '/',
            oldChar: '\\'
        ),
            ["clustering"] = new JsonObject { ["kind"] = "Localhost" },
        };
        var path = Path.Combine(
            path1: directory,
            path2: "silo.json"
        );

        CliFiles.WriteJson(
            path: path,
            value: config
        );
        if (!WorldSiloDefinitionSerialization.TryLoadFile(
            path,
            out _,
            out var refusal
        )) { throw new InvalidDataException(message: refusal); }
        CliFiles.WriteJson(
            path: Path.Combine(
                path1: directory,
                path2: "export.json"
            ),
            value: new JsonObject {
                ["release"] = release.Identity,
                ["capture"] = snapshot?.Identity,
                ["requestId"] = snapshot?.RequestId.ToString(format: "D"),
            }
        );
        await File.WriteAllTextAsync(
            Path.Combine(
                path1: directory,
                path2: "qualification.fixture"
            ),
            "puck.world.qualification.v1",
            token
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
}
