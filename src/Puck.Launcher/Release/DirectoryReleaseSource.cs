using Puck.Assets;

namespace Puck.Launcher.Release;

/// <summary>
/// A release source backed by a local directory — the exact tree <c>puck publish</c>'s dry-run writes: a
/// <c>manifest.json</c> per channel under <c>&lt;root&gt;/&lt;channel&gt;/manifest.json</c>, and payload files under
/// <c>&lt;root&gt;/objects/sha256/&lt;hex[0..2]&gt;/&lt;hex64&gt;</c> — <see cref="ContentAddressedStore"/>'s
/// own layout, so a canary's leg-private install tree and a developer's local dry-run publish read identically. No
/// network, no credential — the loopback twin of <see cref="HttpReleaseSource"/>.
/// </summary>
/// <param name="root">The directory tree's root.</param>
public sealed class DirectoryReleaseSource(string root) : IReleaseSource {
    private readonly string m_root = Path.GetFullPath(path: root);

    /// <summary>Publishes a complete built directory into this source's ordinary content-addressed layout.
    /// Optional signing runs after the complete payload inventory is known.</summary>
    public ReleaseManifest Publish(string input, string rid, ReleaseManifest manifest, Func<ReleaseManifest, ReleaseManifest>? sign = null) {
        var store = new ContentAddressedStore(m_root);
        var directory = Path.GetFullPath(input);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(path => {
            var bytes = File.ReadAllBytes(path);
            return new ReleasePayloadFile(Path.GetRelativePath(directory, path).Replace('\\', '/'), store.Put(bytes).ToString(), bytes.LongLength);
        }).ToArray();
        if (files.Length == 0) { throw new IOException($"No release files under {directory}"); }
        manifest = manifest with { Payloads = [new ReleasePayload(rid, files)] };
        manifest = sign?.Invoke(manifest) ?? manifest;
        var canonical = ReleaseCanonicalizer.Canonicalize(manifest);
        var channel = Path.Combine(m_root, manifest.Channel);
        Directory.CreateDirectory(channel);
        Puck.Assets.AtomicFile.WriteAllBytes(Path.Combine(channel, "manifest.json"), canonical.Bytes);
        return manifest;
    }

    /// <inheritdoc/>
    public async Task<bool> TryGetFileAsync(ContentPin pin, Stream destination, long length, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: destination);
        ArgumentOutOfRangeException.ThrowIfNegative(value: length);

        var objectPath = ContentAddressedStore.ObjectPath(
            pin: pin,
            root: m_root
        );

        if (!File.Exists(path: objectPath)) {
            return false;
        }

        await using var source = File.OpenRead(path: objectPath);

        if (source.Length != length) {
            return false;
        }

        return await ReleaseSourceReader.CopyBoundedAsync(
            cancellationToken: cancellationToken,
            source: source,
            destination: destination,
            maximumBytes: length
        ).ConfigureAwait(continueOnCapturedContext: false) == length;
    }
    /// <inheritdoc/>
    public async Task<ReleaseSourceResult> TryGetLatestManifestAsync(string channel, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: channel);

        var manifestPath = Path.Combine(
            path1: m_root,
            path2: channel,
            path3: "manifest.json"
        );

        if (!File.Exists(path: manifestPath)) {
            return new ReleaseSourceResult(
                Found: false,
                ManifestBytes: [],
                RefusalReason: $"no manifest at '{manifestPath}'"
            );
        }

        await using var source = File.OpenRead(path: manifestPath);
        using var buffer = new MemoryStream();

        if ((source.Length > ReleaseSourceReader.MaximumManifestBytes) ||
            (await ReleaseSourceReader.CopyBoundedAsync(source: source, destination: buffer,
                maximumBytes: ReleaseSourceReader.MaximumManifestBytes, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false) is null)) {
            return new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: "release manifest exceeds the byte limit");
        }

        return new ReleaseSourceResult(
            Found: true,
            ManifestBytes: buffer.ToArray(),
            RefusalReason: null
        );
    }
}
