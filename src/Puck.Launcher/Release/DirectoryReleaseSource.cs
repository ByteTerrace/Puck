namespace Puck.Launcher.Release;

/// <summary>
/// A release source backed by a local directory — the exact tree <c>puck publish</c>'s dry-run writes: a
/// <c>manifest.json</c> per channel under <c>&lt;root&gt;/&lt;channel&gt;/manifest.json</c>, and payload files under
/// <c>&lt;root&gt;/objects/sha256/&lt;hex[0..2]&gt;/&lt;hex64&gt;</c> — <see cref="Puck.Assets.ContentAddressedStore"/>'s
/// own layout, so a canary's leg-private install tree and a developer's local dry-run publish read identically. No
/// network, no credential — the loopback twin of <see cref="HttpReleaseSource"/>.
/// </summary>
/// <param name="root">The directory tree's root.</param>
public sealed class DirectoryReleaseSource(string root) : IReleaseSource {
    private readonly string m_root = Path.GetFullPath(path: root);

    /// <inheritdoc/>
    public Task<ReleaseSourceResult> TryGetLatestManifestAsync(string channel, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: channel);

        var manifestPath = Path.Combine(path1: m_root, path2: channel, path3: "manifest.json");

        if (!File.Exists(path: manifestPath)) {
            return Task.FromResult(result: new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: $"no manifest at '{manifestPath}'"));
        }

        var bytes = File.ReadAllBytes(path: manifestPath);

        return Task.FromResult(result: new ReleaseSourceResult(Found: true, ManifestBytes: bytes, RefusalReason: null));
    }
    /// <inheritdoc/>
    public async Task<bool> TryGetFileAsync(string hash, Stream destination, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: hash);
        ArgumentNullException.ThrowIfNull(argument: destination);

        var objectPath = ContentAddressedLayout.ObjectPath(hash: hash, root: m_root);

        if (!File.Exists(path: objectPath)) {
            return false;
        }

        await using var source = File.OpenRead(path: objectPath);

        await source.CopyToAsync(cancellationToken: cancellationToken, destination: destination).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
}
/// <summary>The <see cref="Puck.Assets.ContentAddressedStore"/> object layout, shared by
/// <see cref="DirectoryReleaseSource"/> and <see cref="ContentAddressedUpdateStager"/> so both sides of a release's
/// file transport address objects identically without either depending on the other.</summary>
public static class ContentAddressedLayout {
    private const string Sha256Prefix = "sha256/";

    /// <summary>Resolves a content hash to its on-disk object path under <paramref name="root"/>.</summary>
    /// <param name="root">The content-addressed store's root directory.</param>
    /// <param name="hash">The object's hash, as <c>sha256/&lt;hex64&gt;</c> or bare <c>&lt;hex64&gt;</c>.</param>
    public static string ObjectPath(string root, string hash) {
        var hex = (hash.StartsWith(comparisonType: StringComparison.Ordinal, value: Sha256Prefix)
            ? hash[Sha256Prefix.Length..]
            : hash
        );

        return Path.Combine(paths: [root, "objects", "sha256", hex[..2], hex]);
    }
}
