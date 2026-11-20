using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Abstractions.Documents;
using Puck.Assets;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

/// <summary>A release's exact deployment inputs: the compute parameters and template, the worker's bootstrap script
/// and the launcher that fetches it. They hold no signing key, so they are retained as an immutable object in the
/// release content store, which the worker's own identity reads; only their pin travels with the keys.</summary>
/// <param name="Release">The full identity of the release the inputs deploy.</param>
/// <param name="Group">The deployment group the inputs belong to.</param>
/// <param name="Parameters">The compute template's parameters, without the protected bootstrap command.</param>
/// <param name="PublicKey">The base64 SubjectPublicKeyInfo of the primary world's federation key.</param>
/// <param name="ComputeTemplate">The compiled compute template.</param>
/// <param name="Bootstrap">The worker's bootstrap script, run once the launcher has verified it.</param>
/// <param name="Launcher">The program the protected bootstrap command runs: it reads these inputs by their pin,
/// verifies them, and runs <paramref name="Bootstrap"/> with the keys.</param>
public sealed record WorldReleaseDeploymentConfiguration(string Release, string Group, JsonObject Parameters, string PublicKey, JsonObject ComputeTemplate,
    string Bootstrap, string Launcher) {
    /// <summary>Gets whether the immutable worker configuration enforces the closed inventory recovery points require.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ClosedGroupRewind { get; init; }
}
/// <summary>A release's retained deployment: its inputs, the pin they are retained under, and the federation signing
/// keys its worker mounts, by file name (base64 PKCS#8).</summary>
/// <param name="Configuration">The deployment inputs.</param>
/// <param name="Inputs">The full content pin of <paramref name="Configuration"/>'s canonical bytes.</param>
/// <param name="Keys">The signing keys by mounted file name.</param>
public sealed record WorldReleaseRetainedDeployment(WorldReleaseDeploymentConfiguration Configuration, string Inputs, IReadOnlyDictionary<string, string> Keys);
/// <summary>A versioned secret backend. Reads always name one immutable version, never the latest secret value.</summary>
public interface IWorldReleaseSecretVersions {
    /// <summary>Writes a new immutable version of the release's secret.</summary>
    /// <param name="release">The full identity of the release the secret belongs to.</param>
    /// <param name="content">The secret bytes.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>The reference of the written version.</returns>
    Task<string> WriteAsync(string release, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    /// <summary>Reads one immutable secret version.</summary>
    /// <param name="version">The version reference <see cref="WriteAsync"/> returned.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>The secret bytes.</returns>
    Task<ReadOnlyMemory<byte>> ReadAsync(string version, CancellationToken cancellationToken);
}
/// <summary>Retains a release's deployment so a replacement controller resumes it exactly. The inputs go to the
/// release content store under their pin; the secret backend holds only the signing keys and that pin; an immutable
/// per-release reference publishes both only after each can be read back. Interrupted writes may leave an
/// unreferenced object or secret version, but cannot publish partial configuration.</summary>
/// <remarks>Invariant: no signing key reaches ordinary storage, and the secret holds nothing but keys and a pin.
/// <see cref="SaveAsync"/> is the one door: it refuses inputs whose bytes contain any key value, and the secret it
/// writes is the private <c>Secret</c> record, which has no other member.</remarks>
public sealed partial class WorldReleaseDeploymentStore(IObjectBlobStore blobs, ObjectStorageTarget target, Guid owner,
    IWorldReleaseSecretVersions secrets) {
    private const string Schema = "puck.world.release-deployment.v2";
    private const string SecretSchema = "puck.world.release-deployment-keys.v1";

    private readonly WorldReleaseArchive m_content = new(
        store: blobs,
        target: target,
        owner: owner
    );

    private sealed record Reference(string Schema, string Release, string Group, string Inputs, string Secret, string SecretVersion);
    private sealed record Secret(string Schema, string Inputs, Dictionary<string, string> Keys);

    /// <summary>The file names a worker's bootstrap accepts for a mounted signing key.</summary>
    [GeneratedRegex(pattern: "\\Afederation(-[0-9a-f]{64})?\\.pk8\\z", options: RegexOptions.CultureInvariant)]
    private static partial Regex KeyFile();

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
    private async Task<WorldReleaseRetainedDeployment> DecodeAsync(ReadOnlyMemory<byte> bytes, WorldReleaseManifest manifest,
        string group, Reference? expected, CancellationToken cancellationToken) {
        if (bytes.Length > (16 * 1024)) { throw new InvalidDataException(message: "retained deployment reference exceeds its byte budget"); }
        var reference = (JsonSerializer.Deserialize<Reference>(bytes.Span) ?? throw new InvalidDataException(message: "empty retained deployment reference"));

        if (
            (reference.Schema != Schema) ||
            (reference.Release != manifest.Identity) ||
            (reference.Group != group) ||
            !ContentPin.TryParse(
            pin: out _,
            text: reference.Inputs
        ) ||
            !ContentPin.TryParse(
            pin: out _,
            text: reference.Secret
        ) ||
            string.IsNullOrWhiteSpace(value: reference.SecretVersion) ||
            !Encode(value: reference).AsSpan().SequenceEqual(other: bytes.Span) ||
            ((expected is not null) && ((expected.Inputs != reference.Inputs) || (expected.Secret != reference.Secret)))
        ) {
            throw new InvalidDataException(message: "retained deployment reference is malformed or conflicts with this release's immutable configuration");
        }
        var inputs = (await m_content.ReadContentAsync(
            cancellationToken: cancellationToken,
            pin: reference.Inputs
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidDataException(message: "retained deployment inputs are missing from the release content store"));
        var configuration = (JsonSerializer.Deserialize<WorldReleaseDeploymentConfiguration>(inputs.Span)
            ?? throw new InvalidDataException(message: "retained deployment inputs are empty"));

        Validate(
            configuration: configuration,
            manifest: manifest
        );
        if (
            (configuration.Group != group) ||
            !Encode(value: configuration).AsSpan().SequenceEqual(other: inputs.Span)
        ) {
            throw new InvalidDataException(message: "retained deployment inputs are not canonical or belong to another group");
        }
        var secret = await secrets.ReadAsync(
            reference.SecretVersion,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (secret.Length > (1024 * 1024)) ||
            (ContentPin.Compute(content: secret.Span).ToString() != reference.Secret)
        ) {
            throw new InvalidDataException(message: "retained deployment secret does not match its full content pin");
        }
        var keys = (JsonSerializer.Deserialize<Secret>(secret.Span) ?? throw new InvalidDataException(message: "retained deployment secret is empty"));

        if (
            (keys.Schema != SecretSchema) ||
            (keys.Inputs != reference.Inputs) ||
            (keys.Keys is null) ||
            !Encode(value: keys).AsSpan().SequenceEqual(other: secret.Span)
        ) {
            throw new InvalidDataException(message: "retained deployment secret is not canonical or belongs to other inputs");
        }
        ValidateKeys(
            inputs: inputs.Span,
            keys: keys.Keys
        );
        return new(
            Configuration: configuration,
            Inputs: reference.Inputs,
            Keys: keys.Keys
        );
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
            string.IsNullOrWhiteSpace(value: configuration.PublicKey) ||
            string.IsNullOrWhiteSpace(value: configuration.Bootstrap) ||
            string.IsNullOrWhiteSpace(value: configuration.Launcher)
        ) {
            throw new InvalidDataException(message: "deployment configuration does not identify the exact release, public key and bootstrap");
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
    private static void ValidateKeys(ReadOnlySpan<byte> inputs, IReadOnlyDictionary<string, string> keys) {
        if (keys.Count == 0) { throw new InvalidDataException(message: "a deployment holds at least one signing key"); }
        var text = System.Text.Encoding.UTF8.GetString(bytes: inputs);

        foreach (var key in keys) {
            if (
                !KeyFile().IsMatch(input: key.Key) ||
                string.IsNullOrEmpty(value: key.Value) ||
                !Convert.TryFromBase64String(
                bytes: new byte[key.Value.Length],
                bytesWritten: out _,
                s: key.Value
            )
            ) {
                throw new InvalidDataException(message: "a deployment signing key needs a federation*.pk8 file name and base64 content");
            }
            if (text.Contains(
                comparisonType: StringComparison.Ordinal,
                value: key.Value
            )) {
                throw new InvalidDataException(message: "deployment inputs contain signing key material; keys belong only in the deployment secret");
            }
        }
    }

    /// <summary>Loads the retained deployment of one release and group, verifying every object against its pin.</summary>
    /// <param name="manifest">The release whose deployment is loaded.</param>
    /// <param name="group">The deployment group.</param>
    /// <param name="cancellationToken">The token that cancels the load.</param>
    /// <returns>The verified deployment, or <see langword="null"/> when none is retained.</returns>
    /// <exception cref="InvalidDataException">The reference, inputs or secret is malformed, missing, or does not match
    /// its pin.</exception>
    public async Task<WorldReleaseRetainedDeployment?> LoadAsync(WorldReleaseManifest manifest, string group, CancellationToken cancellationToken) {
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
    /// <summary>Retains a release's deployment: the inputs in the release content store, the keys and the inputs' pin
    /// in a new secret version, and the reference that publishes both. Saving the same deployment again is a no-op.</summary>
    /// <param name="manifest">The release the deployment belongs to.</param>
    /// <param name="configuration">The deployment inputs; they hold no signing key.</param>
    /// <param name="keys">The signing keys by mounted file name (base64 PKCS#8).</param>
    /// <param name="cancellationToken">The token that cancels the save.</param>
    /// <exception cref="InvalidDataException">The inputs or keys are invalid, the inputs contain a key value, a
    /// different deployment is already retained for the release, or a written object fails read-back.</exception>
    public async Task SaveAsync(WorldReleaseManifest manifest, WorldReleaseDeploymentConfiguration configuration, IReadOnlyDictionary<string, string> keys,
        CancellationToken cancellationToken) {
        Validate(
            configuration: configuration,
            manifest: manifest
        );
        var inputBytes = Encode(value: configuration);

        ValidateKeys(
            inputs: inputBytes,
            keys: keys
        );
        var inputs = ContentPin.Compute(content: inputBytes).ToString();
        var secretBytes = Encode(value: new Secret(
            SecretSchema,
            inputs,
            new Dictionary<string, string>(
                collection: keys,
                comparer: StringComparer.Ordinal
            )
        ));
        var expected = new Reference(
            Schema,
            manifest.Identity,
            configuration.Group,
            inputs,
            ContentPin.Compute(content: secretBytes).ToString(),
            ""
        );
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
                expected,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }
        _ = await m_content.SaveContentAsync(
            bytes: inputBytes,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var version = await secrets.WriteAsync(
            manifest.Identity,
            secretBytes,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (string.IsNullOrWhiteSpace(value: version)) { throw new InvalidDataException(message: "deployment secret backend returned no immutable version"); }
        var readBack = await secrets.ReadAsync(
            cancellationToken: cancellationToken,
            version: version
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!readBack.Span.SequenceEqual(other: secretBytes)) { throw new InvalidDataException(message: "retained deployment secret failed read-back verification"); }
        await blobs.WriteAsync(
            target,
            address,
            Encode(value: expected with { SecretVersion = version }),
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
            expected,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
}
