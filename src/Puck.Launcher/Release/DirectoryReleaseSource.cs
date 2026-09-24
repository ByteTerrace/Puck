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

    /// <inheritdoc/>
    public async Task<bool> TryGetFileAsync(ContentPin pin, Stream destination, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: destination);

        var objectPath = ContentAddressedStore.ObjectPath(
            pin: pin,
            root: m_root
        );

        if (!File.Exists(path: objectPath)) {
            return false;
        }

        await using var source = File.OpenRead(path: objectPath);

        await source.CopyToAsync(
            cancellationToken: cancellationToken,
            destination: destination
        ).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
    /// <inheritdoc/>
    public Task<ReleaseSourceResult> TryGetLatestManifestAsync(string channel, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: channel);

        var manifestPath = Path.Combine(
            path1: m_root,
            path2: channel,
            path3: "manifest.json"
        );

        if (!File.Exists(path: manifestPath)) {
            return Task.FromResult(result: new ReleaseSourceResult(
                Found: false,
                ManifestBytes: [],
                RefusalReason: $"no manifest at '{manifestPath}'"
            ));
        }

        var bytes = File.ReadAllBytes(path: manifestPath);

        return Task.FromResult(result: new ReleaseSourceResult(
            Found: true,
            ManifestBytes: bytes,
            RefusalReason: null
        ));
    }
}
