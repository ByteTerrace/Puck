using System.Net;
using Puck.Assets;

namespace Puck.Launcher.Release;

/// <summary>
/// A release source backed by anonymous HTTP GET against the app's own public-content endpoint — the app's document
/// names this endpoint through its own operational configuration section (e.g. <c>puck.world.definition.v1</c>'s
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

    // ResponseHeadersRead stops HttpClient's timeout at the headers; keep the same deadline over the streamed body.
    private CancellationTokenSource RequestDeadline(CancellationToken cancellationToken) {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

        if (m_httpClient.Timeout != Timeout.InfiniteTimeSpan) {
            deadline.CancelAfter(delay: m_httpClient.Timeout);
        }

        return deadline;
    }

    /// <inheritdoc/>
    public async Task<bool> TryGetFileAsync(ContentPin pin, Stream destination, long length, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: destination);
        ArgumentOutOfRangeException.ThrowIfNegative(value: length);
        using var deadline = RequestDeadline(cancellationToken: cancellationToken);
        cancellationToken = deadline.Token;

        var fileUri = new Uri(
            baseUri: m_baseUri,
            relativeUri: ContentAddressedStore.ObjectRelativePath(pin: pin)
        );

        try {
            using var response = await m_httpClient.GetAsync(
                completionOption: HttpCompletionOption.ResponseHeadersRead,
                cancellationToken: cancellationToken,
                requestUri: fileUri
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!response.IsSuccessStatusCode || ((response.Content.Headers.ContentLength is { } declared) && (declared != length))) {
                return false;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            var copied = await ReleaseSourceReader.CopyBoundedAsync(
                cancellationToken: cancellationToken,
                source: body,
                destination: destination,
                maximumBytes: length
            ).ConfigureAwait(continueOnCapturedContext: false);

            return copied == length;
        } catch (Exception exception) when (exception is HttpRequestException or IOException) {
            return false;
        }
    }
    /// <inheritdoc/>
    public async Task<ReleaseSourceResult> TryGetLatestManifestAsync(string channel, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: channel);
        using var deadline = RequestDeadline(cancellationToken: cancellationToken);
        cancellationToken = deadline.Token;

        var manifestUri = new Uri(
            baseUri: m_baseUri,
            relativeUri: $"{channel}/manifest.json"
        );

        try {
            using var response = await m_httpClient.GetAsync(
                completionOption: HttpCompletionOption.ResponseHeadersRead,
                cancellationToken: cancellationToken,
                requestUri: manifestUri
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (response.StatusCode == HttpStatusCode.NotFound) {
                return new ReleaseSourceResult(
                    Found: false,
                    ManifestBytes: [],
                    RefusalReason: $"no manifest published at '{manifestUri}'"
                );
            }

            if (!response.IsSuccessStatusCode) {
                return new ReleaseSourceResult(
                    Found: false,
                    ManifestBytes: [],
                    RefusalReason: $"'{manifestUri}' returned {((int)response.StatusCode)} {response.StatusCode}"
                );
            }

            if (response.Content.Headers.ContentLength > ReleaseSourceReader.MaximumManifestBytes) {
                return new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: "release manifest exceeds the byte limit");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            using var buffer = new MemoryStream();

            if (await ReleaseSourceReader.CopyBoundedAsync(source: body, destination: buffer,
                maximumBytes: ReleaseSourceReader.MaximumManifestBytes, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false) is null) {
                return new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: "release manifest exceeds the byte limit");
            }

            return new ReleaseSourceResult(
                Found: true,
                ManifestBytes: buffer.ToArray(),
                RefusalReason: null
            );
        } catch (Exception exception) when (exception is HttpRequestException or IOException) {
            return new ReleaseSourceResult(
                Found: false,
                ManifestBytes: [],
                RefusalReason: $"transport error reaching '{manifestUri}': {exception.Message}"
            );
        }
    }
}
