using System.Globalization;
using System.Text;
using Puck.Assets;

namespace Puck.Launcher.Release;

/// <summary>
/// A release payload's complete identity: the content pin of a canonical text naming the identity schema, the payload's
/// runtime identifier, the release's state generation, and every file's path, content pin and size in ordinal path order.
/// Rollout, notes, channel, <see cref="ReleaseManifest.MinimumSupported"/>, revocations, the version label, the archive and
/// the signature are excluded, so re-signing, advancing a rollout or relabelling identical files keeps the identity, and
/// changing any file, the file set or the generation changes it. The install record and the release record
/// (<see cref="Puck.Confinement.ReleasePolicy"/>) carry it; nothing else computes it.
/// </summary>
public static class ReleasePayloadIdentity {
    /// <summary>The identity text's schema line.</summary>
    public const string Schema = "puck.release.payload-identity.v1";

    /// <summary>Computes a payload's identity.</summary>
    /// <param name="payload">The payload; its paths are validated manifest paths, which hold no control characters, so the
    /// tab and newline separators are unambiguous.</param>
    /// <param name="stateGeneration">The release's state generation.</param>
    /// <returns>The identity, as a <c>sha256/&lt;hex&gt;</c> content pin.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    public static string Of(ReleasePayload payload, int stateGeneration) {
        ArgumentNullException.ThrowIfNull(argument: payload);
        var text = new StringBuilder()
            .Append(value: Schema).Append(value: '\n')
            .Append(value: "rid:").Append(value: payload.Rid).Append(value: '\n')
            .Append(value: "stateGeneration:").Append(value: stateGeneration.ToString(provider: CultureInfo.InvariantCulture)).Append(value: '\n');

        foreach (var file in payload.Files.OrderBy(keySelector: static file => file.Path, comparer: StringComparer.Ordinal)) {
            text.Append(value: "file:").Append(value: file.Path).Append(value: '\t').Append(value: file.Hash).Append(value: '\t')
                .Append(value: file.Size.ToString(provider: CultureInfo.InvariantCulture)).Append(value: '\n');
        }
        return ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: text.ToString())).ToString();
    }
    /// <summary>Computes the identity of every payload a manifest carries.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>One identity per payload.</returns>
    public static IEnumerable<string> All(ReleaseManifest manifest) {
        ArgumentNullException.ThrowIfNull(argument: manifest);
        return manifest.Payloads.Select(selector: payload => Of(payload: payload, stateGeneration: manifest.StateGeneration));
    }
}
