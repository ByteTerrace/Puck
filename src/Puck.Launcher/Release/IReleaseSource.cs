namespace Puck.Launcher.Release;

/// <summary>The outcome of asking an <see cref="IReleaseSource"/> for a channel's latest release manifest.</summary>
/// <param name="Found">Whether the source has a manifest for the requested channel.</param>
/// <param name="ManifestBytes">The manifest's raw canonical JSON bytes (with <c>signature</c> populated), when found.</param>
/// <param name="RefusalReason">Why the source could not answer, when <paramref name="Found"/> is false.</param>
public sealed record ReleaseSourceResult(bool Found, byte[] ManifestBytes, string? RefusalReason);
/// <summary>
/// Reaches an <c>app</c>'s published <c>puck.release.v1</c> manifest and the content-addressed files it names. A
/// real transport (<see cref="HttpReleaseSource"/>) and a loopback twin (<see cref="DirectoryReleaseSource"/>) —
/// the same "a real transport plus a loopback twin" shape this repository's own game-protocol layer already
/// establishes for its server link.
/// </summary>
public interface IReleaseSource {
    /// <summary>Fetches the current manifest for <paramref name="channel"/>. Returns a refusal rather than throwing
    /// on a transport failure or a missing channel — the caller decides whether that is fatal.</summary>
    /// <param name="channel">The release channel to fetch.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    Task<ReleaseSourceResult> TryGetLatestManifestAsync(string channel, CancellationToken cancellationToken);
    /// <summary>Fetches one content-addressed payload file's bytes into <paramref name="destination"/>. Never
    /// verifies the hash itself — the caller (<see cref="IUpdateStager"/>) re-verifies every downloaded byte
    /// against the hash the manifest named, so a source implementation is trusted for transport only.</summary>
    /// <param name="hash">The file's content hash, as <c>sha256/&lt;hex64&gt;</c>.</param>
    /// <param name="destination">The stream to write the file's bytes to.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns><see langword="true"/> when the file was found and written; otherwise <see langword="false"/>.</returns>
    Task<bool> TryGetFileAsync(string hash, Stream destination, CancellationToken cancellationToken);
}
