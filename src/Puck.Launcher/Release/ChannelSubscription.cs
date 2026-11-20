using System.Text.Json;

namespace Puck.Launcher.Release;

/// <summary>
/// The release channel an install root follows: where its releases are published, which channel, and the root key it
/// pinned when it was installed. The installer writes it into the install root (<see cref="FileName"/>) on first install
/// and reads it on every later run, and an application running from that install updates from it, so the anchor a
/// consumer trusts is the one it pinned then, never one a later download supplies. It is trusted local configuration:
/// the install root's own user can already replace the binaries it launches.
/// </summary>
/// <param name="Source">The channel tree's root: an <c>http</c> or <c>https</c> URL (<see cref="HttpReleaseSource"/>) or
/// a directory (<see cref="DirectoryReleaseSource"/>).</param>
/// <param name="Channel">The release channel's name.</param>
/// <param name="TrustAnchor">The pinned root key.</param>
public sealed record ChannelSubscription(string Source, string Channel, ReleaseTrustAnchor TrustAnchor) {
    /// <summary>The subscription's file name in an install root.</summary>
    public const string FileName = "channel.json";
    /// <summary>The name, inside a channel's directory, of the trust anchor a publisher writes beside its manifest
    /// (<c>&lt;source&gt;/&lt;channel&gt;/trust.json</c>), which a first install pins.</summary>
    public const string TrustFileName = "trust.json";

    private static readonly JsonSerializerOptions Json = new(defaults: JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Gets whether <see cref="Source"/> is an HTTP URL rather than a directory.</summary>
    public bool IsHttp => IsHttpSource(source: Source);

    /// <summary>Returns whether a channel source names an HTTP URL rather than a directory.</summary>
    /// <param name="source">The source.</param>
    /// <returns><see langword="true"/> for an absolute <c>http</c> or <c>https</c> URL.</returns>
    public static bool IsHttpSource(string source) =>
        Uri.TryCreate(uriString: source, uriKind: UriKind.Absolute, result: out var uri) && ((uri.Scheme == Uri.UriSchemeHttps) || (uri.Scheme == Uri.UriSchemeHttp));

    /// <summary>Builds the release source this subscription names.</summary>
    /// <param name="httpClient">The client an HTTP source fetches through; the caller owns its lifetime.</param>
    /// <returns>The source.</returns>
    public IReleaseSource ToReleaseSource(HttpClient httpClient) => (IsHttp
        ? new HttpReleaseSource(baseUri: new Uri(uriString: Source), httpClient: httpClient)
        : new DirectoryReleaseSource(root: Source));
    /// <summary>Writes the subscription into an install root, replacing the one it held.</summary>
    /// <param name="installRoot">The install root.</param>
    public void Write(string installRoot) {
        Directory.CreateDirectory(path: installRoot);
        Puck.Confinement.AtomicFile.WriteAllBytes(bytes: JsonSerializer.SerializeToUtf8Bytes(options: Json, value: this), path: Path.Combine(path1: installRoot, path2: FileName));
    }
    /// <summary>Reads an install root's subscription.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="subscription">The subscription, when this returns <see langword="true"/>.</param>
    /// <param name="error">Why it could not be read, or <see langword="null"/> when the root holds none.</param>
    /// <returns><see langword="true"/> when the root holds a readable subscription.</returns>
    public static bool TryLoad(string installRoot, out ChannelSubscription? subscription, out string? error) {
        subscription = null;
        error = null;
        var path = Path.Combine(path1: installRoot, path2: FileName);

        if (!File.Exists(path: path)) { return false; }
        try {
            subscription = JsonSerializer.Deserialize<ChannelSubscription>(options: Json, utf8Json: File.ReadAllBytes(path: path));
            if ((subscription is null) || string.IsNullOrWhiteSpace(value: subscription.Source) || string.IsNullOrWhiteSpace(value: subscription.Channel) || (subscription.TrustAnchor is null)) {
                subscription = null;
                error = $"'{path}' does not name a source, a channel and a trust anchor";
                return false;
            }
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) {
            error = exception.Message;
            return false;
        }
    }
    /// <summary>Returns the install root a process runs from: the directory above <c>versions/&lt;version&gt;/</c> that
    /// holds its base directory, or <see langword="null"/> when the process does not run from an install
    /// (<see cref="Puck.Confinement.InstallRegistration.Version"/> is unset).</summary>
    /// <returns>The install root's full path, or <see langword="null"/>.</returns>
    public static string? ProcessInstallRoot() {
        if (Puck.Confinement.InstallRegistration.Process?.Version is null) { return null; }
        var version = Path.TrimEndingDirectorySeparator(path: AppContext.BaseDirectory);

        return Path.GetDirectoryName(path: Path.GetDirectoryName(path: version));
    }
    /// <summary>Serializes a trust anchor as a publisher writes it beside a channel's manifest (<see cref="TrustFileName"/>).</summary>
    /// <param name="anchor">The anchor.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] SerializeAnchor(ReleaseTrustAnchor anchor) => JsonSerializer.SerializeToUtf8Bytes(options: Json, value: anchor);
    /// <summary>Parses a trust anchor a publisher wrote beside a channel's manifest.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>The anchor, or <see langword="null"/> when the bytes do not hold one.</returns>
    public static ReleaseTrustAnchor? ParseAnchor(ReadOnlySpan<byte> bytes) {
        try {
            return ((JsonSerializer.Deserialize<ReleaseTrustAnchor>(options: Json, utf8Json: bytes) is { } anchor) &&
                !string.IsNullOrWhiteSpace(value: anchor.Domain) && !string.IsNullOrWhiteSpace(value: anchor.PublicKeySubjectPublicKeyInfoBase64)) ? anchor : null;
        } catch (JsonException) {
            return null;
        }
    }
}
