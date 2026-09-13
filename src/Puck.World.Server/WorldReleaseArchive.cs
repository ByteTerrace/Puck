using System.Security.Cryptography;
using System.Text.Json;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Retains release manifests and exact package bytes in the existing private object store.
/// Files are immutable full-hash objects; the manifest is published only after every file is present.
/// Engine images remain pinned in their registry, and credentials do not belong in this archive.</summary>
public sealed class WorldReleaseArchive {
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;
    private readonly Guid m_owner;
    private readonly int m_maximumFileBytes;

    public WorldReleaseArchive(IObjectBlobStore store, ObjectStorageTarget target, Guid owner, int maximumFileBytes = 64 * 1024 * 1024) {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_target = target ?? throw new ArgumentNullException(nameof(target));
        if (owner == Guid.Empty) { throw new ArgumentException("A release archive requires an owner.", nameof(owner)); }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        m_owner = owner;
        m_maximumFileBytes = maximumFileBytes;
    }

    /// <summary>Verifies and retains a complete package. An interrupted upload can be retried with the same package;
    /// the manifest never makes a partial upload discoverable as a retained release.</summary>
    public async Task SaveAsync(WorldReleaseManifest manifest, string packageDirectory, CancellationToken cancellationToken = default) {
        // Own the manifest inventories throughout asynchronous publication, even when a caller supplied mutable maps.
        var manifestBytes = WorldReleaseManifest.Canonicalize(manifest);
        var frozen = Decode(manifestBytes, "sha256/" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
        if (!WorldReleaseManifest.TryVerify(frozen, packageDirectory, out var reason)) { throw new InvalidDataException(reason); }
        foreach (var file in Files(frozen)) {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ConfinedFile.ReadAllBytes(Path.Combine(packageDirectory, file.Key), m_maximumFileBytes);
            VerifyPin(bytes, file.Value);
            await WriteImmutableAsync(ContentAddress(file.Value), bytes, cancellationToken).ConfigureAwait(false);
        }
        await WriteImmutableAsync(ManifestAddress(frozen.Identity), manifestBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads one canonical manifest by its exact full-hash identity. It does not claim that the registry image
    /// is retained or the package qualified; those are separate deployment preflight checks.</summary>
    public async Task<WorldReleaseManifest?> LoadAsync(string releaseIdentity, CancellationToken cancellationToken = default) {
        var content = await m_store.ReadAsync(m_target, ManifestAddress(releaseIdentity), cancellationToken).ConfigureAwait(false);
        return content is { } found ? Decode(found.Content.Span, releaseIdentity) : null;
    }

    /// <summary>Reads a declared package file and rechecks its full pin. Unknown paths and missing or corrupt files refuse.</summary>
    public async Task<ReadOnlyMemory<byte>> ReadFileAsync(WorldReleaseManifest manifest, string relativePath, CancellationToken cancellationToken = default) {
        var files = Files(manifest);
        if (!files.TryGetValue(relativePath, out var pin)) { throw new InvalidDataException($"release does not declare '{relativePath}'"); }
        var found = await m_store.ReadAsync(m_target, ContentAddress(pin), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"retained release file '{relativePath}' is missing");
        if (found.Content.Length > m_maximumFileBytes) { throw new InvalidDataException("retained release file exceeds its byte budget"); }
        VerifyPin(found.Content.Span, pin);
        return found.Content;
    }

    /// <summary>Checks every retained file before a deployment may drain the serving release.</summary>
    public async Task VerifyAsync(WorldReleaseManifest manifest, CancellationToken cancellationToken = default) {
        foreach (var path in Files(manifest).Keys) { _ = await ReadFileAsync(manifest, path, cancellationToken).ConfigureAwait(false); }
    }

    private async Task WriteImmutableAsync(ObjectBlobAddress address, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) {
        var result = await m_store.WriteAsync(m_target, address, bytes, ObjectBlobWriteMode.CreateOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) { return; }
        var previous = await m_store.ReadAsync(m_target, address, cancellationToken).ConfigureAwait(false);
        if (previous is not { } existing || !existing.Content.Span.SequenceEqual(bytes.Span)) {
            throw new InvalidDataException("an immutable release archive object is missing or corrupt; it cannot be overwritten");
        }
    }

    private static WorldReleaseManifest Decode(ReadOnlySpan<byte> bytes, string expectedIdentity) {
        VerifyPin(bytes, expectedIdentity);
        try {
            var manifest = JsonSerializer.Deserialize<WorldReleaseManifest>(bytes) ?? throw new InvalidDataException("retained release manifest is empty");
            if (!WorldReleaseManifest.TryValidate(manifest, out var reason)) { throw new InvalidDataException(reason); }
            // Archive input is canonical output, not a tolerant configuration file. This also rejects duplicate,
            // unknown, omitted, or wrongly cased members at every level instead of silently losing their meaning.
            if (!bytes.SequenceEqual(WorldReleaseManifest.Canonicalize(manifest)) || manifest.Schema != WorldReleaseManifest.CurrentSchema) {
                throw new InvalidDataException("retained release manifest is not canonical or uses an unsupported schema");
            }
            _ = Files(manifest);
            return manifest;
        } catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException) {
            throw new InvalidDataException("retained release manifest is malformed", error);
        }
    }

    private static SortedDictionary<string, string> Files(WorldReleaseManifest manifest) {
        if (manifest.Definitions is null || manifest.DefinitionFiles is null || manifest.Artifacts is null || manifest.Definitions.Count == 0 ||
            !manifest.Definitions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.DefinitionFiles.Keys)) {
            throw new InvalidDataException("release file inventory is incomplete");
        }
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in manifest.Definitions) { Add(manifest.DefinitionFiles[definition.Key], definition.Value); }
        foreach (var artifact in manifest.Artifacts) { Add(artifact.Key, artifact.Value); }
        return files;

        void Add(string path, string pin) {
            // The archive addresses bytes by hash. Validate logical paths too, before any consumer materializes them.
            if (path.Contains('\\') || path != ObjectBlobAddressPath.GetNormalizedKey(new(Guid.Empty, path))) {
                throw new InvalidDataException("release file paths must use canonical relative spelling");
            }
            _ = Digest(pin);
            if (files.TryGetValue(path, out var prior) && prior != pin) { throw new InvalidDataException($"release file '{path}' has conflicting pins"); }
            files[path] = pin;
        }
    }

    private ObjectBlobAddress ContentAddress(string pin) => new(m_owner, $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/content/{Digest(pin)}");
    private ObjectBlobAddress ManifestAddress(string pin) => new(m_owner, $"{WorldOwnedWorldSync.HostedPrivateNamespace}/releases/manifests/{Digest(pin)}.json");
    private static string Digest(string pin) => pin is { Length: 71 } && pin.StartsWith("sha256/", StringComparison.Ordinal) && pin[7..].All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')
        ? pin[7..] : throw new InvalidDataException("release archive requires a full lowercase SHA-256 pin");
    private static void VerifyPin(ReadOnlySpan<byte> bytes, string expected) {
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != Digest(expected)) { throw new InvalidDataException("retained release content does not match its pin"); }
    }
}
