using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Server;

/// <summary>Immutable identity and compatibility claims for one hosted world release.</summary>
public sealed record WorldReleaseManifest {
    /// <summary>The first release manifest schema.</summary>
    public const string CurrentSchema = "puck.world.release.v1";

    /// <summary>The manifest schema.</summary>
    [JsonPropertyName("schema")] public string Schema { get; init; } = CurrentSchema;
    /// <summary>Human-readable operator label.</summary>
    [JsonPropertyName("label")] public required string Label { get; init; }
    /// <summary>Source revision used to build the release.</summary>
    [JsonPropertyName("sourceRevision")] public required string SourceRevision { get; init; }
    /// <summary>Immutable engine image digest.</summary>
    [JsonPropertyName("engineImageDigest")] public required string EngineImageDigest { get; init; }
    /// <summary>Canonical composed world definition pins keyed by stable world identity.</summary>
    [JsonPropertyName("definitions")] public IReadOnlyDictionary<string, string> Definitions { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    /// <summary>Required release artifact pins keyed by their package-relative path.</summary>
    [JsonPropertyName("artifacts")] public IReadOnlyDictionary<string, string> Artifacts { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    /// <summary>Persistence encoding contract shared by this release pair.</summary>
    [JsonPropertyName("persistenceContract")] public required string PersistenceContract { get; init; }
    /// <summary>Peer protocol contract shared by this release pair.</summary>
    [JsonPropertyName("peerProtocolContract")] public required string PeerProtocolContract { get; init; }

    /// <summary>Returns the canonical, content-addressed identity of this manifest.</summary>
    [JsonIgnore] public string Identity => ComputeIdentity(this);

    /// <summary>Computes the full SHA-256 identity over canonical manifest content.</summary>
    public static string ComputeIdentity(WorldReleaseManifest manifest) {
        ArgumentNullException.ThrowIfNull(manifest);
        var bytes = Canonicalize(manifest);
        return "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Serializes a manifest in stable property and dictionary order.</summary>
    public static byte[] Canonicalize(WorldReleaseManifest manifest) {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = new JsonObjectBuilder(manifest);
        return Encoding.UTF8.GetBytes(json.ToJson());
    }

    /// <summary>Validates a manifest and all artifact hashes in a package directory.</summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <param name="packageDirectory">The package root containing referenced artifacts.</param>
    /// <param name="reason">The refusal reason, empty on success.</param>
    public static bool TryVerify(WorldReleaseManifest manifest, string packageDirectory, out string reason) {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        if (!string.Equals(manifest.Schema, CurrentSchema, StringComparison.Ordinal)) { reason = $"unsupported release manifest schema '{manifest.Schema}'"; return false; }
        if (string.IsNullOrWhiteSpace(manifest.Label) || string.IsNullOrWhiteSpace(manifest.SourceRevision)) { reason = "release label and source revision are required"; return false; }
        if (!IsImageDigest(manifest.EngineImageDigest)) { reason = "engine image must be an immutable sha256 digest"; return false; }
        if (string.IsNullOrWhiteSpace(manifest.PersistenceContract) || string.IsNullOrWhiteSpace(manifest.PeerProtocolContract)) { reason = "persistence and peer protocol contracts are required"; return false; }
        foreach (var pair in manifest.Definitions.Concat(manifest.Artifacts)) {
            if (string.IsNullOrWhiteSpace(pair.Key) || Path.IsPathRooted(pair.Key) || pair.Key.Contains("..", StringComparison.Ordinal)) { reason = $"release artifact path '{pair.Key}' is not package-relative"; return false; }
            if (!IsFullHash(pair.Value)) { reason = $"release artifact '{pair.Key}' does not carry a full sha256 pin"; return false; }
            var path = Path.GetFullPath(Path.Combine(packageDirectory, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) { reason = $"release artifact '{pair.Key}' is missing"; return false; }
            var actual = "sha256/" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(actual, pair.Value, StringComparison.Ordinal)) { reason = $"release artifact '{pair.Key}' hashes to '{actual}', expected '{pair.Value}'"; return false; }
        }
        reason = string.Empty;
        return true;
    }

    private static bool IsImageDigest(string value) => value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(Uri.IsHexDigit);
    private static bool IsFullHash(string value) => value.StartsWith("sha256/", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(Uri.IsHexDigit);

    private sealed class JsonObjectBuilder(WorldReleaseManifest manifest) {
        public string ToJson() {
            var root = new SortedDictionary<string, object?>(StringComparer.Ordinal) {
                ["artifacts"] = new SortedDictionary<string, string>(manifest.Artifacts, StringComparer.Ordinal),
                ["definitions"] = new SortedDictionary<string, string>(manifest.Definitions, StringComparer.Ordinal),
                ["engineImageDigest"] = manifest.EngineImageDigest,
                ["label"] = manifest.Label,
                ["peerProtocolContract"] = manifest.PeerProtocolContract,
                ["persistenceContract"] = manifest.PersistenceContract,
                ["schema"] = manifest.Schema,
                ["sourceRevision"] = manifest.SourceRevision,
            };
            return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = false });
        }
    }
}

/// <summary>Compatibility policy for the first official release workflow.</summary>
public static class WorldReleaseCompatibility {
    /// <summary>Refuses a pair that cannot share its authoritative state encoding and peer protocol.</summary>
    public static bool TryQualify(WorldReleaseManifest previous, WorldReleaseManifest candidate, out string reason) {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.Equals(previous.Identity, candidate.Identity, StringComparison.Ordinal)) { reason = "release is identical to the active release"; return false; }
        if (!string.Equals(previous.PersistenceContract, candidate.PersistenceContract, StringComparison.Ordinal)) { reason = "persistence contract is not qualified in both directions"; return false; }
        if (!string.Equals(previous.PeerProtocolContract, candidate.PeerProtocolContract, StringComparison.Ordinal)) { reason = "peer protocol contract is not qualified in both directions"; return false; }
        if (previous.Definitions.Keys.Except(candidate.Definitions.Keys, StringComparer.Ordinal).Any() || candidate.Definitions.Keys.Except(previous.Definitions.Keys, StringComparer.Ordinal).Any()) { reason = "release definition inventory changes are not admitted"; return false; }
        reason = string.Empty;
        return true;
    }
}
