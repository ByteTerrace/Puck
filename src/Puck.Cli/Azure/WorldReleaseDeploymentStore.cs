using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

/// <summary>Exact deployment inputs retained in a versioned secret. Parameters can contain bootstrap credentials;
/// only their digest and secret version reference belong in ordinary release storage.</summary>
internal sealed record WorldReleaseDeploymentConfiguration(string Release, string Group, JsonObject Parameters, string PublicKey, JsonObject ComputeTemplate);

/// <summary>A versioned secret backend. Reads always name one immutable version, never the latest secret value.</summary>
internal interface IWorldReleaseSecretVersions {
    Task<string> WriteAsync(string release, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    Task<ReadOnlyMemory<byte>> ReadAsync(string version, CancellationToken cancellationToken);
}

/// <summary>Publishes an immutable per-release deployment reference only after its secret version can be read back.
/// Interrupted writes may leave an unreferenced secret version, but cannot publish partial configuration.</summary>
internal sealed class WorldReleaseDeploymentStore(IObjectBlobStore blobs, ObjectStorageTarget target, Guid owner,
    IWorldReleaseSecretVersions secrets) {
    private const string Schema = "puck.world.release-deployment.v1";
    private sealed record Reference(string Schema, string Release, string Group, string ContentHash, string SecretVersion);

    public async Task SaveAsync(WorldReleaseManifest manifest, WorldReleaseDeploymentConfiguration configuration, CancellationToken cancellationToken) {
        Validate(manifest, configuration);
        var bytes = Encode(configuration);
        if (bytes.Length > 1024 * 1024) { throw new InvalidDataException("deployment configuration exceeds its byte budget"); }
        var pin = Hash(bytes);
        var address = Address(configuration.Group, manifest.Identity);
        if (await blobs.ReadAsync(target, address, cancellationToken).ConfigureAwait(false) is { } existing) {
            _ = await DecodeAsync(existing.Content, manifest, configuration.Group, pin, cancellationToken).ConfigureAwait(false);
            return;
        }
        var version = await secrets.WriteAsync(manifest.Identity, bytes, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(version)) { throw new InvalidDataException("deployment secret backend returned no immutable version"); }
        var readBack = await secrets.ReadAsync(version, cancellationToken).ConfigureAwait(false);
        if (!readBack.Span.SequenceEqual(bytes)) { throw new InvalidDataException("retained deployment secret failed read-back verification"); }
        var reference = Encode(new Reference(Schema, manifest.Identity, configuration.Group, pin, version));
        await blobs.WriteAsync(target, address, reference, ObjectBlobWriteMode.CreateOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
        // Read even after success: a lost response or competing publisher must resolve to the same immutable inputs.
        var published = await blobs.ReadAsync(target, address, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("retained deployment reference was not published");
        _ = await DecodeAsync(published.Content, manifest, configuration.Group, pin, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorldReleaseDeploymentConfiguration?> LoadAsync(WorldReleaseManifest manifest, string group, CancellationToken cancellationToken) {
        var content = await blobs.ReadAsync(target, Address(group, manifest.Identity), cancellationToken).ConfigureAwait(false);
        return content is { } found ? await DecodeAsync(found.Content, manifest, group, null, cancellationToken).ConfigureAwait(false) : null;
    }

    private async Task<WorldReleaseDeploymentConfiguration> DecodeAsync(ReadOnlyMemory<byte> bytes, WorldReleaseManifest manifest,
        string group, string? expectedHash, CancellationToken cancellationToken) {
        if (bytes.Length > 16 * 1024) { throw new InvalidDataException("retained deployment reference exceeds its byte budget"); }
        var reference = JsonSerializer.Deserialize<Reference>(bytes.Span) ?? throw new InvalidDataException("empty retained deployment reference");
        if (reference.Schema != Schema || reference.Release != manifest.Identity || reference.Group != group ||
            !IsHash(reference.ContentHash) || string.IsNullOrWhiteSpace(reference.SecretVersion) ||
            !Encode(reference).AsSpan().SequenceEqual(bytes.Span) || (expectedHash is not null && expectedHash != reference.ContentHash)) {
            throw new InvalidDataException("retained deployment reference is malformed or conflicts with this release's immutable configuration");
        }
        var secret = await secrets.ReadAsync(reference.SecretVersion, cancellationToken).ConfigureAwait(false);
        if (secret.Length > 1024 * 1024 || Hash(secret.Span) != reference.ContentHash) {
            throw new InvalidDataException("retained deployment secret does not match its full content pin");
        }
        var configuration = JsonSerializer.Deserialize<WorldReleaseDeploymentConfiguration>(secret.Span)
            ?? throw new InvalidDataException("retained deployment secret is empty");
        Validate(manifest, configuration);
        if (configuration.Group != group || !Encode(configuration).AsSpan().SequenceEqual(secret.Span)) {
            throw new InvalidDataException("retained deployment secret is not canonical or belongs to another group");
        }
        return configuration;
    }

    private ObjectBlobAddress Address(string group, string release) {
        if (owner == Guid.Empty || !IsHash(release)) { throw new InvalidDataException("deployment retention requires an owner and full release identity"); }
        _ = SafeName.Parse(group);
        return new(owner, $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/deployments/{group}/{release[7..]}.json");
    }

    private static void Validate(WorldReleaseManifest manifest, WorldReleaseDeploymentConfiguration configuration) {
        if (!WorldReleaseManifest.TryValidate(manifest, out var reason)) { throw new InvalidDataException(reason); }
        if (configuration.Release != manifest.Identity || configuration.Parameters is null || configuration.ComputeTemplate is null || string.IsNullOrWhiteSpace(configuration.PublicKey)) {
            throw new InvalidDataException("deployment configuration does not identify the exact release and public key");
        }
        _ = SafeName.Parse(configuration.Group);
        var image = configuration.Parameters["release"]?.GetValue<string>();
        if (image is null || !image.EndsWith("@" + manifest.EngineImageDigest, StringComparison.Ordinal)) {
            throw new InvalidDataException("deployment image does not match the manifest's exact registry digest");
        }
    }

    private static byte[] Encode<T>(T value) => CanonicalJsonDocument.Serialize(Sort(JsonSerializer.SerializeToNode(value))!);
    private static JsonNode? Sort(JsonNode? node) => node switch {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Sort(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        _ => node?.DeepClone(),
    };
    private static string Hash(ReadOnlySpan<byte> bytes) => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsHash(string? pin) => pin is { Length: 71 } && pin.StartsWith("sha256/", StringComparison.Ordinal) && pin.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
}
