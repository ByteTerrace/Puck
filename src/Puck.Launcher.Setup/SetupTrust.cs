using Puck.Attestation;
using Puck.Launcher.Release;

namespace Puck.Launcher.Setup;

/// <summary>Reads the root key a channel publishes beside its manifest for a first install to pin.</summary>
public static class SetupTrust {
    // A trust file is a few hundred bytes; anything far larger is not one.
    private const int MaximumBytes = (64 * 1024);

    /// <summary>Fetches and checks the anchor at <c>&lt;source&gt;/&lt;channel&gt;/trust.json</c>: it must parse, name
    /// a signing algorithm this verifier knows, and carry the fingerprint of its own key.</summary>
    /// <param name="source">The channel tree's root: a URL or a directory.</param>
    /// <param name="channel">The channel's name.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The anchor, or why it was refused.</returns>
    public static async Task<(ReleaseTrustAnchor? Anchor, string? Refusal)> FetchAnchorAsync(string source, string channel, CancellationToken cancellationToken) {
        var location = $"{source.TrimEnd('/')}/{channel}/{ChannelSubscription.TrustFileName}";
        byte[] bytes;

        try {
            if (ChannelSubscription.IsHttpSource(source: source)) {
                using var response = await HttpReleaseSource.SharedClient.GetAsync(cancellationToken: cancellationToken, completionOption: HttpCompletionOption.ResponseHeadersRead, requestUri: location).ConfigureAwait(continueOnCapturedContext: false);

                if (!response.IsSuccessStatusCode) { return (null, $"{location} answered {((int)response.StatusCode)} {response.StatusCode}; is the channel published?"); }
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                bytes = (await Puck.Confinement.FileContent.ReadAtMostAsync(cancellationToken: cancellationToken, maximumBytes: MaximumBytes, stream: body).ConfigureAwait(continueOnCapturedContext: false)
                    ?? throw new InvalidDataException(message: $"{location} is larger than a trust file."));
            } else {
                await using var file = File.OpenRead(path: location);
                bytes = (Puck.Confinement.FileContent.ReadAtMost(maximumBytes: MaximumBytes, stream: file) ?? throw new InvalidDataException(message: $"{location} is larger than a trust file."));
            }
        } catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException) {
            return (null, $"the channel's root key could not be read from {location}: {exception.Message}");
        }

        if (ChannelSubscription.ParseAnchor(bytes: bytes) is not { } anchor) { return (null, $"{location} does not hold a trust anchor."); }
        if (!string.Equals(a: anchor.Algorithm, b: AttestationAlgorithms.EcdsaP256Sha256, comparisonType: StringComparison.Ordinal)) {
            return (null, $"{location} names the signing algorithm '{anchor.Algorithm}', which this installer does not verify.");
        }
        try {
            var fingerprint = KeyId.ForRoot(algorithm: anchor.Algorithm, subjectPublicKeyInfo: Convert.FromBase64String(s: anchor.PublicKeySubjectPublicKeyInfoBase64)).Domain;

            if (!string.Equals(a: fingerprint, b: anchor.Domain, comparisonType: StringComparison.Ordinal)) {
                return (null, $"{location} names fingerprint {anchor.Domain} for a key whose fingerprint is {fingerprint}.");
            }
        } catch (FormatException exception) {
            return (null, $"{location} holds no readable key: {exception.Message}");
        }
        return (anchor, null);
    }
}
