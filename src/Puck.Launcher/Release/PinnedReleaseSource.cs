using Puck.Assets;

namespace Puck.Launcher.Release;

/// <summary>
/// A release source that answers one exact signed manifest, read by its content pin from the channel's object space,
/// instead of the channel's latest. <c>puck channel push</c> stores every signed manifest it publishes as an object
/// beside the files it names, so a release stays installable by its pin after the channel moves on: a hosting machine
/// that Azure replaces long after its deployment installs the release that deployment admitted, not whatever the channel
/// serves by then. The manifest's bytes are checked against the pin here; the update chain then verifies its signature
/// against the install's pinned root key, as it does for every manifest, and each file against the pin the manifest
/// names.
/// </summary>
/// <param name="inner">The channel's source, which serves the manifest object and the files.</param>
/// <param name="pin">The content pin of the signed manifest's bytes.</param>
/// <param name="length">The signed manifest's exact byte length.</param>
public sealed class PinnedReleaseSource(IReleaseSource inner, ContentPin pin, long length) : IReleaseSource {
    /// <inheritdoc/>
    public Task<bool> TryGetFileAsync(ContentPin pin, Stream destination, long length, CancellationToken cancellationToken) =>
        inner.TryGetFileAsync(cancellationToken: cancellationToken, destination: destination, length: length, pin: pin);
    /// <inheritdoc/>
    public async Task<ReleaseSourceResult> TryGetLatestManifestAsync(string channel, CancellationToken cancellationToken) {
        if ((length <= 0) || (length > ReleaseSourceReader.MaximumManifestBytes)) {
            return new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: $"a pinned manifest of {length} bytes is outside the manifest byte limit");
        }
        using var buffer = new MemoryStream(capacity: (int)length);

        if (!await inner.TryGetFileAsync(cancellationToken: cancellationToken, destination: buffer, length: length, pin: pin).ConfigureAwait(continueOnCapturedContext: false)) {
            return new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: $"the channel holds no manifest object {pin}");
        }
        var bytes = buffer.ToArray();

        return ((ContentPin.Compute(content: bytes) == pin)
            ? new ReleaseSourceResult(Found: true, ManifestBytes: bytes, RefusalReason: null)
            : new ReleaseSourceResult(Found: false, ManifestBytes: [], RefusalReason: $"the manifest object read for {pin} does not match its pin"));
    }
}
