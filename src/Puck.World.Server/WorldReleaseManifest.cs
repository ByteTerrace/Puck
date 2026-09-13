using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Immutable identity and compatibility claims for one hosted world release.</summary>
public sealed record WorldReleaseManifest {
    /// <summary>The first release manifest schema.</summary>
    public const string CurrentSchema = "puck.world.release.v1";
    /// <summary>Requires package-qualified metadata transformation and atomic definition/checkpoint publication.</summary>
    public const string MetadataCoordinatorContract = "puck.world.release.metadata.v1";
    /// <summary>Requires receipt-preserving exports and packaged lookup/duplicate qualification as well as metadata publication.</summary>
    public const string CurrentCoordinatorContract = "puck.world.release.receipts.v1";

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
    /// <summary>Package-relative source paths for definitions, kept separate from stable world identities.</summary>
    [JsonPropertyName("definitionFiles")] public IReadOnlyDictionary<string, string> DefinitionFiles { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    /// <summary>Required release artifact pins keyed by their package-relative path.</summary>
    [JsonPropertyName("artifacts")] public IReadOnlyDictionary<string, string> Artifacts { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    /// <summary>Persistence encoding contract shared by this release pair.</summary>
    [JsonPropertyName("persistenceContract")] public required string PersistenceContract { get; init; }
    /// <summary>Peer protocol contract shared by this release pair.</summary>
    [JsonPropertyName("peerProtocolContract")] public required string PeerProtocolContract { get; init; }
    /// <summary>Required coordinator behavior. Absence preserves existing engine-only manifest identities.
    /// Older archive readers reject a manifest carrying an unknown canonical member before resuming it.</summary>
    [JsonPropertyName("coordinatorContract"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoordinatorContract { get; init; }

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

    /// <summary>Validates manifest metadata, inventories, portable relative paths, and full content pins.</summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <param name="reason">The refusal reason, empty on success.</param>
    public static bool TryValidate(WorldReleaseManifest manifest, out string reason) {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Schema, CurrentSchema, StringComparison.Ordinal)) {
            reason = $"unsupported release manifest schema '{manifest.Schema}'";
            return false;
        }
        if (manifest.CoordinatorContract is not null && manifest.CoordinatorContract is not (MetadataCoordinatorContract or CurrentCoordinatorContract)) {
            reason = $"unsupported release coordinator contract '{manifest.CoordinatorContract}'; use tooling that supports this package";
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.Label) || string.IsNullOrWhiteSpace(manifest.SourceRevision)) {
            reason = "release label and source revision are required";
            return false;
        }
        if (!IsImageDigest(manifest.EngineImageDigest)) {
            reason = "engine image must be an immutable sha256 digest";
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.PersistenceContract) || string.IsNullOrWhiteSpace(manifest.PeerProtocolContract)) {
            reason = "persistence and peer protocol contracts are required";
            return false;
        }
        if (manifest.Definitions is null || manifest.DefinitionFiles is null || manifest.Artifacts is null) {
            reason = "release manifest inventories must be objects";
            return false;
        }
        if (manifest.Definitions.Count == 0) {
            reason = "release manifest must contain at least one composed world definition";
            return false;
        }
        if (!manifest.Definitions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.DefinitionFiles.Keys)) {
            reason = "release definitionFiles inventory must exactly match definitions inventory";
            return false;
        }
        foreach (var pair in manifest.Definitions.OrderBy(static pair => pair.Key, StringComparer.Ordinal)) {
            if (string.IsNullOrWhiteSpace(pair.Key) || !IsFullHash(pair.Value)) {
                reason = $"release definition '{pair.Key}' has an invalid stable identity or hash";
                return false;
            }
            if (!manifest.DefinitionFiles.TryGetValue(pair.Key, out var definitionPath)) {
                reason = $"release definition '{pair.Key}' has no package path";
                return false;
            }
            if (!ValidateFilePath(definitionPath, pair.Value, "definition", out reason)) {
                return false;
            }
        }
        foreach (var pair in manifest.Artifacts.OrderBy(static pair => pair.Key, StringComparer.Ordinal)) {
            if (!ValidateFilePath(pair.Key, pair.Value, "artifact", out reason)) {
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Validates a manifest and every referenced file's full hash in a package directory.</summary>
    public static bool TryVerify(WorldReleaseManifest manifest, string packageDirectory, out string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        if (!TryValidate(manifest, out reason)) { return false; }
        foreach (var definition in manifest.Definitions) {
            if (!VerifyFile(packageDirectory, manifest.DefinitionFiles[definition.Key], definition.Value, "definition", out reason)) { return false; }
        }
        foreach (var artifact in manifest.Artifacts) {
            if (!VerifyFile(packageDirectory, artifact.Key, artifact.Value, "artifact", out reason)) { return false; }
        }
        return true;
    }

    private static bool ValidateFilePath(string relativeName, string expected, string kind, out string reason) {
        if (string.IsNullOrWhiteSpace(relativeName)) { reason = $"release {kind} path is empty"; return false; }
        try {
            if (relativeName.Contains('\\') || relativeName != ObjectBlobAddressPath.GetNormalizedKey(new(Guid.Empty, relativeName))) {
                reason = $"release {kind} path '{relativeName}' is not a canonical relative path";
                return false;
            }
        } catch (ArgumentException) {
            reason = $"release {kind} path '{relativeName}' is not a portable relative path";
            return false;
        }
        if (!IsFullHash(expected)) {
            reason = $"release {kind} '{relativeName}' does not carry a full sha256 pin";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool VerifyFile(string packageDirectory, string relativeName, string expected, string kind, out string reason) {
        var path = Path.GetFullPath(Path.Combine(packageDirectory, relativeName.Replace('/', Path.DirectorySeparatorChar)));
        var packageRoot = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(packageRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(path)) {
            reason = $"release {kind} '{relativeName}' is missing";
            return false;
        }
        if (HasReparsePoint(packageDirectory, path)) {
            reason = $"release {kind} '{relativeName}' uses a symlink or reparse point";
            return false;
        }
        var actual = "sha256/" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) {
            reason = $"release {kind} '{relativeName}' hashes to '{actual}', expected '{expected}'";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool HasReparsePoint(string packageDirectory, string path) {
        var root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var current = path;
        while (current.Length >= root.Length) {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) {
                return true;
            }
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) {
                break;
            }
            current = Path.GetDirectoryName(current) ?? root;
        }
        return false;
    }

    private static bool IsImageDigest(string? value) => value is not null && value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(Uri.IsHexDigit);
    private static bool IsFullHash(string? value) => value is not null && value.StartsWith("sha256/", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(Uri.IsHexDigit);

    private sealed class JsonObjectBuilder(WorldReleaseManifest manifest) {
        public string ToJson() {
            var root = new SortedDictionary<string, object?>(StringComparer.Ordinal) {
                ["artifacts"] = manifest.Artifacts.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                ["definitions"] = manifest.Definitions.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                ["definitionFiles"] = manifest.DefinitionFiles.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                ["engineImageDigest"] = manifest.EngineImageDigest,
                ["label"] = manifest.Label,
                ["peerProtocolContract"] = manifest.PeerProtocolContract,
                ["persistenceContract"] = manifest.PersistenceContract,
                ["schema"] = manifest.Schema,
                ["sourceRevision"] = manifest.SourceRevision,
            };
            if (manifest.CoordinatorContract is not null) { root["coordinatorContract"] = manifest.CoordinatorContract; }
            return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = false });
        }
    }
}

/// <summary>Structural compatibility checks for the first official release workflow. Passing this check is not a
/// qualification claim: packaged cross-release exercise evidence is still required before deployment.</summary>
public static class WorldReleaseCompatibility {
    /// <summary>Requires at least one immutable manifest in a metadata pair to exclude older coordinators.
    /// Both manifests are loaded before runtime effects, so this also protects rollback to a legacy manifest.</summary>
    public static bool TryRequireMetadataCoordinator(WorldReleaseManifest source, WorldReleaseManifest target, out string reason) {
        if (!WorldReleaseManifest.TryValidate(source, out reason) || !WorldReleaseManifest.TryValidate(target, out reason)) { return false; }
        if (source.CoordinatorContract is not (WorldReleaseManifest.MetadataCoordinatorContract or WorldReleaseManifest.CurrentCoordinatorContract) &&
            target.CoordinatorContract is not (WorldReleaseManifest.MetadataCoordinatorContract or WorldReleaseManifest.CurrentCoordinatorContract)) {
            reason = "metadata transitions require a package with the metadata coordinator contract; prepare the new release with current tooling";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Checks structural prerequisites only. This does not qualify a deployment or prove state preservation.</summary>
    public static bool TryCheckStructuralCompatibility(WorldReleaseManifest previous, WorldReleaseManifest candidate, out string reason) {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.Equals(previous.Identity, candidate.Identity, StringComparison.Ordinal)) {
            reason = "release is identical to the active release";
            return false;
        }
        if (!string.Equals(previous.PersistenceContract, candidate.PersistenceContract, StringComparison.Ordinal)) {
            reason = "persistence contract differs between the release pair";
            return false;
        }
        if (!string.Equals(previous.PeerProtocolContract, candidate.PeerProtocolContract, StringComparison.Ordinal)) {
            reason = "peer protocol contract differs between the release pair";
            return false;
        }
        if (previous.Definitions.Keys.Except(candidate.Definitions.Keys, StringComparer.Ordinal).Any() || candidate.Definitions.Keys.Except(previous.Definitions.Keys, StringComparer.Ordinal).Any()) {
            reason = "release definition inventory changes are not structurally compatible";
            return false;
        }
        if (!SameInventory(previous.DefinitionFiles, candidate.DefinitionFiles) || !SameInventory(previous.Artifacts, candidate.Artifacts)) {
            reason = "release artifact inventory or pins differ; engine and metadata transitions cannot change packaged dependencies";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool SameInventory(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) => left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && string.Equals(item.Value, value, StringComparison.Ordinal));
}
