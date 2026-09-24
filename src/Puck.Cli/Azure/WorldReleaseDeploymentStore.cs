using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Assets;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

/// <summary>Exact deployment inputs retained in a versioned secret. Parameters can contain bootstrap credentials;
/// only their digest and secret version reference belong in ordinary release storage.</summary>
internal sealed record WorldReleaseDeploymentConfiguration(string Release, string Group, JsonObject Parameters, string PublicKey, JsonObject ComputeTemplate) {
    /// <summary>The immutable worker configuration enforces the closed inventory required by recovery points.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ClosedGroupRewind { get; init; }
}
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

    private ObjectBlobAddress Address(string group, string release) {
        if (
            (owner == Guid.Empty) ||
            !ContentPin.TryParse(
            pin: out var releasePin,
            text: release
        )
        ) { throw new InvalidDataException(message: "deployment retention requires an owner and full release identity"); }
        _ = SafeName.Parse(candidate: group);
        return new(
            owner,
            $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/deployments/{group}/{releasePin.Hex}.json"
        );
    }
    private async Task<WorldReleaseDeploymentConfiguration> DecodeAsync(ReadOnlyMemory<byte> bytes, WorldReleaseManifest manifest,
        string group, string? expectedHash, CancellationToken cancellationToken) {
        if (bytes.Length > (16 * 1024)) { throw new InvalidDataException(message: "retained deployment reference exceeds its byte budget"); }
        var reference = (JsonSerializer.Deserialize<Reference>(bytes.Span) ?? throw new InvalidDataException(message: "empty retained deployment reference"));

        if (
            (reference.Schema != Schema) ||
            (reference.Release != manifest.Identity) ||
            (reference.Group != group) ||
            !ContentPin.TryParse(
            pin: out _,
            text: reference.ContentHash
        ) ||
            string.IsNullOrWhiteSpace(value: reference.SecretVersion) ||
            !Encode(value: reference).AsSpan().SequenceEqual(other: bytes.Span) ||
            ((expectedHash is not null) && (expectedHash != reference.ContentHash))
        ) {
            throw new InvalidDataException(message: "retained deployment reference is malformed or conflicts with this release's immutable configuration");
        }
        var secret = await secrets.ReadAsync(
            reference.SecretVersion,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (secret.Length > (1024 * 1024)) ||
            (ContentPin.Compute(content: secret.Span).ToString() != reference.ContentHash)
        ) {
            throw new InvalidDataException(message: "retained deployment secret does not match its full content pin");
        }
        var configuration = (JsonSerializer.Deserialize<WorldReleaseDeploymentConfiguration>(secret.Span)
            ?? throw new InvalidDataException(message: "retained deployment secret is empty"));

        Validate(
            configuration: configuration,
            manifest: manifest
        );
        if (
            (configuration.Group != group) ||
            !Encode(value: configuration).AsSpan().SequenceEqual(other: secret.Span)
        ) {
            throw new InvalidDataException(message: "retained deployment secret is not canonical or belongs to another group");
        }
        return configuration;
    }
    private static byte[] Encode<T>(T value) => CanonicalJsonDocument.Serialize(node: Sort(node: JsonSerializer.SerializeToNode(value))!);
    private static JsonNode? Sort(JsonNode? node) => node switch {
        JsonObject obj => new JsonObject(obj.OrderBy(
        pair => pair.Key,
        StringComparer.Ordinal
    ).Select(selector: pair => new KeyValuePair<string, JsonNode?>(
        key: pair.Key,
        value: Sort(node: pair.Value)
    ))),
        JsonArray array => new JsonArray(array.Select(selector: Sort).ToArray()),
        _ => node?.DeepClone(),
    };
    private static void Validate(WorldReleaseManifest manifest, WorldReleaseDeploymentConfiguration configuration) {
        if (!WorldReleaseManifest.TryValidate(
            manifest: manifest,
            reason: out var reason
        )) { throw new InvalidDataException(message: reason); }
        if (
            (configuration.Release != manifest.Identity) ||
            (configuration.Parameters is null) ||
            (configuration.ComputeTemplate is null) ||
            string.IsNullOrWhiteSpace(value: configuration.PublicKey)
        ) {
            throw new InvalidDataException(message: "deployment configuration does not identify the exact release and public key");
        }
        _ = SafeName.Parse(candidate: configuration.Group);
        var image = configuration.Parameters["release"]?.GetValue<string>();

        if (
            (image is null) ||
            !image.EndsWith(
            ("@" + manifest.EngineImageDigest),
            StringComparison.Ordinal
        )
        ) {
            throw new InvalidDataException(message: "deployment image does not match the manifest's exact registry digest");
        }
    }

    public async Task<WorldReleaseDeploymentConfiguration?> LoadAsync(WorldReleaseManifest manifest, string group, CancellationToken cancellationToken) {
        var content = await blobs.ReadAsync(
            target,
            Address(
                group: group,
                release: manifest.Identity
            ),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return ((content is { } found)
            ? await DecodeAsync(
                found.Content,
                manifest,
                group,
                null,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false)
            : null
        );
    }
    public async Task SaveAsync(WorldReleaseManifest manifest, WorldReleaseDeploymentConfiguration configuration, CancellationToken cancellationToken) {
        Validate(
            configuration: configuration,
            manifest: manifest
        );
        var bytes = Encode(value: configuration);

        if (bytes.Length > (1024 * 1024)) { throw new InvalidDataException(message: "deployment configuration exceeds its byte budget"); }
        var pin = ContentPin.Compute(content: bytes).ToString();
        var address = Address(
            group: configuration.Group,
            release: manifest.Identity
        );

        if (await blobs.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false) is { } existing) {
            _ = await DecodeAsync(
                existing.Content,
                manifest,
                configuration.Group,
                pin,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }
        var version = await secrets.WriteAsync(
            manifest.Identity,
            bytes,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (string.IsNullOrWhiteSpace(value: version)) { throw new InvalidDataException(message: "deployment secret backend returned no immutable version"); }
        var readBack = await secrets.ReadAsync(
            cancellationToken: cancellationToken,
            version: version
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!readBack.Span.SequenceEqual(other: bytes)) { throw new InvalidDataException(message: "retained deployment secret failed read-back verification"); }
        var reference = Encode(value: new Reference(
            Schema,
            manifest.Identity,
            configuration.Group,
            pin,
            version
        ));

        await blobs.WriteAsync(
            target,
            address,
            reference,
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        // Read even after success: a lost response or competing publisher must resolve to the same immutable inputs.
        var published = (await blobs.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new IOException(message: "retained deployment reference was not published"));

        _ = await DecodeAsync(
            published.Content,
            manifest,
            configuration.Group,
            pin,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
}
