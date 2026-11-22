using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>Materializes a disposable, credential-free qualification store from a complete retained capture.
/// Bootstrap publishes each world's exact release definition through an unowned authority root and creates no
/// gameplay state; the marker is published last.</summary>
public sealed class WorldReleaseFixtureBuilder(IObjectBlobStore blobs, WorldReleaseArchive releases, WorldReleaseFixtureArchive fixtures) {
    /// <summary>Builds the qualification fixture for <paramref name="release"/> from <paramref name="snapshot"/>, or a
    /// bootstrap fixture without gameplay when <paramref name="snapshot"/> is <see langword="null"/>. A snapshot whose
    /// state this build cannot decode (<see cref="WorldReleaseCoordinator.FindStateReset"/>) builds
    /// <paramref name="successor"/>'s bootstrap fixture instead, so a deployment resets rather than refuses; with no
    /// successor it refuses. Nothing is written before that decision.</summary>
    /// <param name="release">The release whose definitions the fixture holds: the deployment's source when a snapshot is
    /// given, else the release being bootstrapped.</param>
    /// <param name="owner">The owner of every world in the release.</param>
    /// <param name="snapshot">The source's retained capture, or <see langword="null"/> for a bootstrap fixture.</param>
    /// <param name="directory">The fresh directory the fixture is written to.</param>
    /// <param name="token">The token that cancels reads and writes.</param>
    /// <param name="successor">The release a deployment starts fresh when the snapshot's state cannot be decoded, or
    /// <see langword="null"/> to refuse such a snapshot.</param>
    /// <returns>Why the snapshot's state was reset and the successor's bootstrap fixture built, or
    /// <see langword="null"/> when the fixture holds what was asked for.</returns>
    /// <exception cref="InvalidDataException">The snapshot does not match the release's inventory, or its state cannot
    /// be decoded and no successor was given.</exception>
    /// <exception cref="IOException"><paramref name="directory"/> already exists, or a fixture write is refused.</exception>
    public async Task<string?> BuildAsync(WorldReleaseManifest release, Guid owner, WorldReleaseFixtureManifest? snapshot,
        string directory, CancellationToken token, WorldReleaseManifest? successor = null) {
        directory = Path.GetFullPath(path: directory);
        if (
            Directory.Exists(path: directory) ||
            File.Exists(path: directory)
        ) { throw new IOException(message: "qualification export requires a fresh output directory"); }
        _ = CliWorldVocabulary.EnsureInstalled();
        var checkpoints = new SortedDictionary<string, ReadOnlyMemory<byte>>(comparer: StringComparer.Ordinal);
        string? reset = null;

        if (snapshot is not null) {
            foreach (var world in snapshot.Worlds.Keys) {
                checkpoints.Add(
                    key: world,
                    value: await fixtures.ReadCheckpointAsync(
                        snapshot,
                        world,
                        token
                    ).ConfigureAwait(continueOnCapturedContext: false)
                );
            }
            reset = WorldReleaseCoordinator.FindStateReset(checkpoints: checkpoints);
            if (reset is not null) {
                if (successor is null) {
                    throw new InvalidDataException(message: $"qualification snapshot checkpoint refused — {reset}. Capture a new release fixture with the current authority.");
                }
                release = successor;
                snapshot = null;
            }
        }
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
        var target = WorldAuthorityBlobStore.DirectoryTarget(rootPath: Path.Combine(
            path1: directory,
            path2: "store"
        ));
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
                var published = await authority.PublishDefinitionBytesAsync(
                    cancellationToken: token,
                    definition: definitionBytes,
                    identity: identity
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!published.Ok) { throw new IOException(message: ("bootstrap fixture definition could not be published: " + published.Detail)); }
            } else {
                var checkpointBytes = checkpoints[world.Key];

                if (!WorldAuthorityCheckpointCodec.TryDecode(
                    bytes: checkpointBytes.Span,
                    checkpoint: out var captured,
                    reason: out var reason
                )) {
                    throw new InvalidDataException(message: $"qualification snapshot checkpoint refused — {reason}");
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
            var keyPath = Puck.Abstractions.PuckPaths.Slashed(path: Path.Combine(
                path1: keys.FullName,
                path2: $"{owner:D}-{world.Key}.pk8"
            ));

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
            ["schema"] = Puck.World.WorldSiloDefinition.SchemaVersion,
            ["worlds"] = rows,
            ["doors"] = new JsonObject { ["budget"] = rows.Count },
            ["store"] = new JsonObject {
                ["type"] = "directory",
                ["settings"] = new JsonObject {
                    ["path"] = Puck.Abstractions.PuckPaths.Slashed(path: Path.Combine(
            path1: directory,
            path2: "store"
        )),
                },
            },
            ["stateDir"] = Puck.Abstractions.PuckPaths.Slashed(path: state.FullName),
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
            WorldReleaseQualificationRunner.MarkerFileContent,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);
        return reset;
    }
}
