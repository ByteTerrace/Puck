using System.Security.Cryptography;
using System.Text.Json;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Retains release manifests and exact package bytes in the existing private object store.
/// Files are immutable full-hash objects; the manifest is published only after every file is present.
/// Engine images remain pinned in their registry, and credentials do not belong in this archive.</summary>
public sealed class WorldReleaseArchive {
    private readonly int m_maximumFileBytes;
    private readonly Guid m_owner;
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;

    public WorldReleaseArchive(IObjectBlobStore store, ObjectStorageTarget target, Guid owner, int maximumFileBytes = ((64 * 1024) * 1024)) {
        m_store = (store ?? throw new ArgumentNullException(paramName: nameof(store)));
        m_target = (target ?? throw new ArgumentNullException(paramName: nameof(target)));
        if (owner == Guid.Empty) { throw new ArgumentException(
            message: "A release archive requires an owner.",
            paramName: nameof(owner)
        ); }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        m_owner = owner;
        m_maximumFileBytes = maximumFileBytes;
    }

    private ObjectBlobAddress ContentAddress(string pin) => new(
        m_owner,
        $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/content/{Digest(pin: pin)}"
    );
    private static WorldReleaseManifest Decode(ReadOnlySpan<byte> bytes, string expectedIdentity) {
        VerifyPin(
            bytes: bytes,
            expected: expectedIdentity
        );
        try {
            var manifest = (JsonSerializer.Deserialize<WorldReleaseManifest>(bytes) ?? throw new InvalidDataException(message: "retained release manifest is empty"));

            if (!WorldReleaseManifest.TryValidate(
                manifest: manifest,
                reason: out var reason
            )) { throw new InvalidDataException(message: reason); }
            // Archive input is canonical output, not a tolerant configuration file. This also rejects duplicate,
            // unknown, omitted, or wrongly cased members at every level instead of silently losing their meaning.
            if (
                !bytes.SequenceEqual(other: WorldReleaseManifest.Canonicalize(manifest: manifest)) ||
                (manifest.Schema != WorldReleaseManifest.CurrentSchema)
            ) {
                throw new InvalidDataException(message: "retained release manifest is not canonical or uses an unsupported schema");
            }
            _ = Files(manifest: manifest);
            return manifest;
        } catch (Exception error) when ((error is JsonException or ArgumentException or NullReferenceException)) {
            throw new InvalidDataException(
                innerException: error,
                message: "retained release manifest is malformed"
            );
        }
    }
    private static string Digest(string pin) => (((pin is { Length: 71 }) && pin.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "sha256/"
    ) && pin[7..].All(predicate: c => (char.IsAsciiDigit(c: c) || (c is >= 'a' and <= 'f'))))
        ? pin[7..]
        : throw new InvalidDataException(message: "release archive requires a full lowercase SHA-256 pin")
    );
    private static SortedDictionary<string, string> Files(WorldReleaseManifest manifest) {
        if (
            (manifest.Definitions is null) ||
            (manifest.DefinitionFiles is null) ||
            (manifest.Artifacts is null) ||
            (manifest.Definitions.Count == 0) ||
            !manifest.Definitions.Keys.ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: manifest.DefinitionFiles.Keys)
        ) {
            throw new InvalidDataException(message: "release file inventory is incomplete");
        }
        var files = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var definition in manifest.Definitions) { Add(
            path: manifest.DefinitionFiles[definition.Key],
            pin: definition.Value
        ); }
        foreach (var artifact in manifest.Artifacts) { Add(
            path: artifact.Key,
            pin: artifact.Value
        ); }
        return files;

        void Add(string path, string pin) {
            // The archive addresses bytes by hash. Validate logical paths too, before any consumer materializes them.
            if (
                path.Contains(value: '\\') ||
                (path != ObjectBlobAddressPath.GetNormalizedKey(address: new(
                Key: path,
                ObjectId: Guid.Empty
            )))
            ) {
                throw new InvalidDataException(message: "release file paths must use canonical relative spelling");
            }
            _ = Digest(pin: pin);
            if (
                files.TryGetValue(
                key: path,
                value: out var prior
            ) &&
                (prior != pin)
            ) { throw new InvalidDataException(message: $"release file '{path}' has conflicting pins"); }
            files[path] = pin;
        }
    }
    private ObjectBlobAddress ManifestAddress(string pin) => new(
        m_owner,
        $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/manifests/{Digest(pin: pin)}.json"
    );
    private static void VerifyPin(ReadOnlySpan<byte> bytes, string expected) {
        if (Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)) != Digest(pin: expected)) { throw new InvalidDataException(message: "retained release content does not match its pin"); }
    }
    private async Task WriteImmutableAsync(ObjectBlobAddress address, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) {
        var result = await m_store.WriteAsync(
            m_target,
            address,
            bytes,
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (result.Succeeded) { return; }
        var previous = await m_store.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: m_target
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (previous is not { } existing) ||
            !existing.Content.Span.SequenceEqual(other: bytes.Span)
        ) {
            throw new InvalidDataException(message: "an immutable release archive object is missing or corrupt; it cannot be overwritten");
        }
    }

    /// <summary>Loads one canonical manifest by its exact full-hash identity. It does not claim that the registry image
    /// is retained or the package qualified; those are separate deployment preflight checks.</summary>
    public async Task<WorldReleaseManifest?> LoadAsync(string releaseIdentity, CancellationToken cancellationToken = default) {
        var content = await m_store.ReadAsync(
            m_target,
            ManifestAddress(pin: releaseIdentity),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return ((content is { } found)
            ? Decode(
                bytes: found.Content.Span,
                expectedIdentity: releaseIdentity
            )
            : null
        );
    }
    /// <summary>Reads a declared package file and rechecks its full pin. Unknown paths and missing or corrupt files refuse.</summary>
    public async Task<ReadOnlyMemory<byte>> ReadFileAsync(WorldReleaseManifest manifest, string relativePath, CancellationToken cancellationToken = default) {
        var files = Files(manifest: manifest);

        if (!files.TryGetValue(
            key: relativePath,
            value: out var pin
        )) { throw new InvalidDataException(message: $"release does not declare '{relativePath}'"); }
        var found = (await m_store.ReadAsync(
            m_target,
            ContentAddress(pin: pin),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidDataException(message: $"retained release file '{relativePath}' is missing"));

        if (found.Content.Length > m_maximumFileBytes) { throw new InvalidDataException(message: "retained release file exceeds its byte budget"); }
        VerifyPin(
            bytes: found.Content.Span,
            expected: pin
        );
        return found.Content;
    }
    /// <summary>Verifies and retains a complete package. An interrupted upload can be retried with the same package;
    /// the manifest never makes a partial upload discoverable as a retained release.</summary>
    public async Task SaveAsync(WorldReleaseManifest manifest, string packageDirectory, CancellationToken cancellationToken = default) {
        // Own the manifest inventories throughout asynchronous publication, even when a caller supplied mutable maps.
        var manifestBytes = WorldReleaseManifest.Canonicalize(manifest: manifest);
        var frozen = Decode(
            bytes: manifestBytes,
            expectedIdentity: ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: manifestBytes)))
        );

        if (!WorldReleaseManifest.TryVerify(
            manifest: frozen,
            packageDirectory: packageDirectory,
            reason: out var reason
        )) { throw new InvalidDataException(message: reason); }
        foreach (var file in Files(manifest: frozen)) {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ConfinedFile.ReadAllBytes(
                Path.Combine(
                    path1: packageDirectory,
                    path2: file.Key
                ),
                m_maximumFileBytes
            );

            VerifyPin(
                bytes: bytes,
                expected: file.Value
            );
            await WriteImmutableAsync(
                address: ContentAddress(pin: file.Value),
                bytes: bytes,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        await WriteImmutableAsync(
            address: ManifestAddress(pin: frozen.Identity),
            bytes: manifestBytes,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Checks every retained file before a deployment may drain the serving release.</summary>
    public async Task VerifyAsync(WorldReleaseManifest manifest, CancellationToken cancellationToken = default) {
        foreach (var path in Files(manifest: manifest).Keys) { _ = await ReadFileAsync(
            cancellationToken: cancellationToken,
            manifest: manifest,
            relativePath: path
        ).ConfigureAwait(continueOnCapturedContext: false); }
    }
}
