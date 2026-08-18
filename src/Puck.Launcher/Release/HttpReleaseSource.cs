using System.Net;

namespace Puck.Launcher.Release;

/// <summary>
/// A release source backed by anonymous HTTP GET against the app's own public-content endpoint — the app's document
/// names this endpoint through its own operational configuration section (e.g. <c>puck.world.def.v1</c>'s
/// <c>update</c> section), never a <c>PUCK_*</c> environment variable. Layout mirrors <see cref="DirectoryReleaseSource"/>'s directory tree over
/// HTTP: <c>&lt;baseUri&gt;/&lt;channel&gt;/manifest.json</c> and
/// <c>&lt;baseUri&gt;/objects/sha256/&lt;hex[0..2]&gt;/&lt;hex64&gt;</c>. Genuinely distinct from
/// <c>Puck.Storage</c> (credentialed Azure SDK, per-user containers) and <c>Puck.Assets.IAssetSource</c>
/// (synchronous, path-addressed, no network) — a release is anonymous, cached, public-content data.
/// </summary>
/// <param name="httpClient">The client to issue requests through. The caller owns its lifetime.</param>
/// <param name="baseUri">The release channel tree's base URI (no trailing slash required).</param>
public sealed class HttpReleaseSource(HttpClient httpClient, Uri baseUri) : IReleaseSource {
    private readonly Uri m_baseUri = new(uriString: (baseUri.OriginalString.TrimEnd(trimChar: '/') + "/"));
    private readonly HttpClient m_httpClient = httpClient;

    /// <inheritdoc/>
    public async Task<ReleaseSourceResult> TryGetLatestManifestAsync(string channel, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: channel);

        var manifestUri = new Uri(baseUri: m_baseUri, relativeUri: $"{channel}/manifest.json");

        try {
            using var response = await m_httpClient.GetAsync(cancellationToken: cancellationToken, requestUri: manifestUri).ConfigureAwait(continueOnCapturedContext: false);

            if (response.StatusCode == HttpStatusCode.NotFound) {
                return new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: $"no manifest published at '{manifestUri}'");
            }

            if (!response.IsSuccessStatusCode) {
                return new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: $"'{manifestUri}' returned {((int)response.StatusCode)} {response.StatusCode}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            return new ReleaseSourceResult(Found: true, ManifestBytes: bytes, RefusalReason: null);
        } catch (HttpRequestException exception) {
            return new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: $"transport error reaching '{manifestUri}': {exception.Message}");
        }
    }
    /// <inheritdoc/>
    public async Task<bool> TryGetFileAsync(string hash, Stream destination, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: hash);
        ArgumentNullException.ThrowIfNull(argument: destination);

        var hex = (hash.StartsWith(comparisonType: StringComparison.Ordinal, value: "sha256/") ? hash["sha256/".Length..] : hash);
        var fileUri = new Uri(baseUri: m_baseUri, relativeUri: $"objects/sha256/{hex[..2]}/{hex}");

        try {
            using var response = await m_httpClient.GetAsync(cancellationToken: cancellationToken, requestUri: fileUri).ConfigureAwait(continueOnCapturedContext: false);

            if (!response.IsSuccessStatusCode) {
                return false;
            }

            await response.Content.CopyToAsync(cancellationToken: cancellationToken, stream: destination).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        } catch (HttpRequestException) {
            return false;
        }
    }
}
