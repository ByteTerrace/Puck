using System.Text.Json;
using Puck.Attestation;

namespace Puck.Launcher.Release;

/// <summary>
/// A composition root's test/ops-only override for the facets <see cref="UpdateOptions"/> otherwise sources from the
/// app's own document: the release-source directory, the trust anchor, and (only when a leg needs its own disposable
/// install tree rather than the document-authored one) the cache root. A production composition root always calls
/// <see cref="LauncherServiceRegistration.AddSelfUpdate"/> — channel, cache root, check interval, and keep-N-versions
/// come from the app's own durable document (a world's <c>update</c> section, for <c>Puck.World</c>) and the trust
/// anchor is a build-pinned constant. This bundle exists only so a test, canary, or operator can point a real build
/// at a throwaway signing chain, a leg-private release-source directory, and (when needed) a leg-private install
/// root without a conditionally-compiled bypass — the same control-plane category as <c>--federation-key-file</c>.
/// </summary>
/// <param name="ReleaseSourceDirectory">A <see cref="DirectoryReleaseSource"/> root — the loopback twin of the
/// app's real public-content endpoint.</param>
/// <param name="TrustAnchorDomain">The pinned root key's own SHA-256 fingerprint (lowercase hex).</param>
/// <param name="TrustAnchorAlgorithm">The pinned key's signing algorithm, from <see cref="AttestationAlgorithms"/>.</param>
/// <param name="TrustAnchorPublicKeySubjectPublicKeyInfoBase64">The pinned root key's SPKI bytes, base64-encoded.</param>
/// <param name="CacheRoot">Overrides the document-resolved cache root when present — needed only when a leg's own
/// stub-managed install tree must sit somewhere other than the document's authored (or default) cache root.</param>
public sealed record SelfUpdateConfigFile(
    string ReleaseSourceDirectory,
    string TrustAnchorDomain,
    string TrustAnchorAlgorithm,
    string TrustAnchorPublicKeySubjectPublicKeyInfoBase64,
    string? CacheRoot = null
) {
    /// <summary>Builds the <see cref="ReleaseTrustAnchor"/> this bundle names.</summary>
    public ReleaseTrustAnchor ToTrustAnchor() => new(
        Algorithm: TrustAnchorAlgorithm,
        Domain: TrustAnchorDomain,
        PublicKeySubjectPublicKeyInfoBase64: TrustAnchorPublicKeySubjectPublicKeyInfoBase64
    );
    /// <summary>Builds the <see cref="DirectoryReleaseSource"/> this bundle points at.</summary>
    public DirectoryReleaseSource ToReleaseSource() => new(root: ReleaseSourceDirectory);
    /// <summary>Reads and parses a self-update config file.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="config">The parsed bundle, when this returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this returns <see langword="false"/>.</param>
    public static bool TryLoad(string path, out SelfUpdateConfigFile? config, out string? error) {
        try {
            config = (JsonSerializer.Deserialize<SelfUpdateConfigFile>(json: File.ReadAllText(path: path))
                ?? throw new JsonException(message: "deserialized to null"));
            error = null;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException)) {
            config = null;
            error = exception.Message;

            return false;
        }
    }
}
