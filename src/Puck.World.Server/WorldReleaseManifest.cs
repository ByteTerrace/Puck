using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Immutable identity and compatibility claims for one hosted world release.</summary>
public sealed record WorldReleaseManifest {
    /// <summary>Also requires closed-group rewind boundary enforcement and resumable explicit restore.</summary>
    public const string CurrentCoordinatorContract = "puck.world.release.restore.v1";
    /// <summary>The first release manifest schema.</summary>
    public const string CurrentSchema = "puck.world.release.v1";
    /// <summary>Requires package-qualified metadata transformation and atomic definition/checkpoint publication.</summary>
    public const string MetadataCoordinatorContract = "puck.world.release.metadata.v1";
    /// <summary>Requires receipt-preserving exports and packaged lookup/duplicate qualification as well as metadata publication.</summary>
    public const string ReceiptCoordinatorContract = "puck.world.release.receipts.v1";

    /// <summary>The manifest schema.</summary>
    [JsonPropertyName("schema")] public string Schema { get; init; } = CurrentSchema;
    /// <summary>Canonical composed world definition pins keyed by stable world identity.</summary>
    [JsonPropertyName("definitions")] public IReadOnlyDictionary<string, string> Definitions { get; init; } = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
    /// <summary>Package-relative source paths for definitions, kept separate from stable world identities.</summary>
    [JsonPropertyName("definitionFiles")] public IReadOnlyDictionary<string, string> DefinitionFiles { get; init; } = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
    /// <summary>Required release artifact pins keyed by their package-relative path.</summary>
    [JsonPropertyName("artifacts")] public IReadOnlyDictionary<string, string> Artifacts { get; init; } = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

    /// <summary>Required coordinator behavior. Absence preserves existing engine-only manifest identities.
    /// Older archive readers reject a manifest carrying an unknown canonical member before resuming it.</summary>
    [JsonPropertyName("coordinatorContract"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoordinatorContract { get; init; }
    /// <summary>Immutable engine image digest.</summary>
    [JsonPropertyName("engineImageDigest")] public required string EngineImageDigest { get; init; }
    /// <summary>Returns the canonical, content-addressed identity of this manifest.</summary>
    [JsonIgnore] public string Identity => ComputeIdentity(manifest: this);
    /// <summary>Human-readable operator label.</summary>
    [JsonPropertyName("label")] public required string Label { get; init; }
    /// <summary>Peer protocol contract shared by this release pair.</summary>
    [JsonPropertyName("peerProtocolContract")] public required string PeerProtocolContract { get; init; }
    /// <summary>Persistence encoding contract shared by this release pair.</summary>
    [JsonPropertyName("persistenceContract")] public required string PersistenceContract { get; init; }
    /// <summary>Source revision used to build the release.</summary>
    [JsonPropertyName("sourceRevision")] public required string SourceRevision { get; init; }

    private static bool HasReparsePoint(string packageDirectory, string path) {
        var root = Path.GetFullPath(path: packageDirectory).TrimEnd(trimChar: Path.DirectorySeparatorChar);
        var current = path;

        while (current.Length >= root.Length) {
            if ((File.GetAttributes(path: current) & FileAttributes.ReparsePoint) != 0) {
                return true;
            }
            if (string.Equals(
                a: current,
                b: root,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                break;
            }
            current = (Path.GetDirectoryName(path: current) ?? root);
        }
        return false;
    }
    private static bool IsFullHash(string? value) => ((value is not null) && value.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "sha256/"
    ) && (value.Length == 71) && value[7..].All(predicate: Uri.IsHexDigit));
    private static bool IsImageDigest(string? value) => ((value is not null) && value.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "sha256:"
    ) && (value.Length == 71) && value[7..].All(predicate: Uri.IsHexDigit));
    private static bool ValidateFilePath(string relativeName, string expected, string kind, out string reason) {
        if (string.IsNullOrWhiteSpace(value: relativeName)) { reason = $"release {kind} path is empty"; return false; }
        try {
            if (
                relativeName.Contains(value: '\\') ||
                (relativeName != ObjectBlobAddressPath.GetNormalizedKey(address: new(
                Key: relativeName,
                ObjectId: Guid.Empty
            )))
            ) {
                reason = $"release {kind} path '{relativeName}' is not a canonical relative path";
                return false;
            }
        } catch (ArgumentException) {
            reason = $"release {kind} path '{relativeName}' is not a portable relative path";
            return false;
        }
        if (!IsFullHash(value: expected)) {
            reason = $"release {kind} '{relativeName}' does not carry a full sha256 pin";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    private static bool VerifyFile(string packageDirectory, string relativeName, string expected, string kind, out string reason) {
        var path = Path.GetFullPath(path: Path.Combine(
            path1: packageDirectory,
            path2: relativeName.Replace(
                newChar: Path.DirectorySeparatorChar,
                oldChar: '/'
            )
        ));
        var packageRoot = (Path.GetFullPath(path: packageDirectory).TrimEnd(trimChar: Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

        if (
            !path.StartsWith(
            packageRoot,
            (OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal)
        ) ||
            !File.Exists(path: path)
        ) {
            reason = $"release {kind} '{relativeName}' is missing";
            return false;
        }
        if (HasReparsePoint(
            packageDirectory: packageDirectory,
            path: path
        )) {
            reason = $"release {kind} '{relativeName}' uses a symlink or reparse point";
            return false;
        }
        var actual = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: File.ReadAllBytes(path: path))));

        if (!string.Equals(
            a: actual,
            b: expected,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"release {kind} '{relativeName}' hashes to '{actual}', expected '{expected}'";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Serializes a manifest in stable property and dictionary order.</summary>
    public static byte[] Canonicalize(WorldReleaseManifest manifest) {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = new JsonObjectBuilder(manifest: manifest);

        return Encoding.UTF8.GetBytes(s: json.ToJson());
    }
    /// <summary>Computes the full SHA-256 identity over canonical manifest content.</summary>
    public static string ComputeIdentity(WorldReleaseManifest manifest) {
        ArgumentNullException.ThrowIfNull(manifest);
        var bytes = Canonicalize(manifest: manifest);

        return ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)));
    }
    /// <summary>Validates manifest metadata, inventories, portable relative paths, and full content pins.</summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <param name="reason">The refusal reason, empty on success.</param>
    public static bool TryValidate(WorldReleaseManifest manifest, out string reason) {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(
            a: manifest.Schema,
            b: CurrentSchema,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"unsupported release manifest schema '{manifest.Schema}'";
            return false;
        }
        if (
            (manifest.CoordinatorContract is not null) &&
            (manifest.CoordinatorContract is not (MetadataCoordinatorContract or ReceiptCoordinatorContract or CurrentCoordinatorContract))
        ) {
            reason = $"unsupported release coordinator contract '{manifest.CoordinatorContract}'; use tooling that supports this package";
            return false;
        }
        if (
            string.IsNullOrWhiteSpace(value: manifest.Label) ||
            string.IsNullOrWhiteSpace(value: manifest.SourceRevision)
        ) {
            reason = "release label and source revision are required";
            return false;
        }
        if (!IsImageDigest(value: manifest.EngineImageDigest)) {
            reason = "engine image must be an immutable sha256 digest";
            return false;
        }
        if (
            string.IsNullOrWhiteSpace(value: manifest.PersistenceContract) ||
            string.IsNullOrWhiteSpace(value: manifest.PeerProtocolContract)
        ) {
            reason = "persistence and peer protocol contracts are required";
            return false;
        }
        if (
            (manifest.Definitions is null) ||
            (manifest.DefinitionFiles is null) ||
            (manifest.Artifacts is null)
        ) {
            reason = "release manifest inventories must be objects";
            return false;
        }
        if (manifest.Definitions.Count == 0) {
            reason = "release manifest must contain at least one composed world definition";
            return false;
        }
        if (!manifest.Definitions.Keys.ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: manifest.DefinitionFiles.Keys)) {
            reason = "release definitionFiles inventory must exactly match definitions inventory";
            return false;
        }
        foreach (var pair in manifest.Definitions.OrderBy(
            static pair => pair.Key,
            StringComparer.Ordinal
        )) {
            if (
                string.IsNullOrWhiteSpace(value: pair.Key) ||
                !IsFullHash(value: pair.Value)
            ) {
                reason = $"release definition '{pair.Key}' has an invalid stable identity or hash";
                return false;
            }
            if (!manifest.DefinitionFiles.TryGetValue(
                key: pair.Key,
                value: out var definitionPath
            )) {
                reason = $"release definition '{pair.Key}' has no package path";
                return false;
            }
            if (!ValidateFilePath(
                definitionPath,
                pair.Value,
                "definition",
                out reason
            )) {
                return false;
            }
        }
        foreach (var pair in manifest.Artifacts.OrderBy(
            static pair => pair.Key,
            StringComparer.Ordinal
        )) {
            if (!ValidateFilePath(
                pair.Key,
                pair.Value,
                "artifact",
                out reason
            )) {
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Validates a manifest and every referenced file's full hash in a package directory.</summary>
    public static bool TryVerify(WorldReleaseManifest manifest, string packageDirectory, out string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        if (!TryValidate(
            manifest: manifest,
            reason: out reason
        )) { return false; }
        foreach (var definition in manifest.Definitions) {
            if (!VerifyFile(
                packageDirectory,
                manifest.DefinitionFiles[definition.Key],
                definition.Value,
                "definition",
                out reason
            )) { return false; }
        }
        foreach (var artifact in manifest.Artifacts) {
            if (!VerifyFile(
                packageDirectory,
                artifact.Key,
                artifact.Value,
                "artifact",
                out reason
            )) { return false; }
        }
        return true;
    }

    private sealed class JsonObjectBuilder(WorldReleaseManifest manifest) {
        public string ToJson() {
            var root = new SortedDictionary<string, object?>(comparer: StringComparer.Ordinal) {
                ["artifacts"] = manifest.Artifacts.OrderBy(
                static pair => pair.Key,
                StringComparer.Ordinal
            ).ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal
            ),
                ["definitions"] = manifest.Definitions.OrderBy(
                static pair => pair.Key,
                StringComparer.Ordinal
            ).ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal
            ),
                ["definitionFiles"] = manifest.DefinitionFiles.OrderBy(
                static pair => pair.Key,
                StringComparer.Ordinal
            ).ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal
            ),
                ["engineImageDigest"] = manifest.EngineImageDigest,
                ["label"] = manifest.Label,
                ["peerProtocolContract"] = manifest.PeerProtocolContract,
                ["persistenceContract"] = manifest.PersistenceContract,
                ["schema"] = manifest.Schema,
                ["sourceRevision"] = manifest.SourceRevision,
            };

            if (manifest.CoordinatorContract is not null) { root["coordinatorContract"] = manifest.CoordinatorContract; }
            return JsonSerializer.Serialize(
                root,
                new JsonSerializerOptions { WriteIndented = false }
            );
        }
    }
}
/// <summary>Structural compatibility checks for the first official release workflow. Passing this check is not a
/// qualification claim: packaged cross-release exercise evidence is still required before deployment.</summary>
public static class WorldReleaseCompatibility {
    private static bool SameInventory(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) => ((left.Count == right.Count) && left.All(predicate: item => (right.TryGetValue(
        key: item.Key,
        value: out var value
    ) && string.Equals(
        a: item.Value,
        b: value,
        comparisonType: StringComparison.Ordinal
    ))));

    /// <summary>Checks structural prerequisites only. This does not qualify a deployment or prove state preservation.</summary>
    public static bool TryCheckStructuralCompatibility(WorldReleaseManifest previous, WorldReleaseManifest candidate, out string reason) {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.Equals(
            a: previous.Identity,
            b: candidate.Identity,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = "release is identical to the active release";
            return false;
        }
        if (!string.Equals(
            a: previous.PersistenceContract,
            b: candidate.PersistenceContract,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = "persistence contract differs between the release pair";
            return false;
        }
        if (!string.Equals(
            a: previous.PeerProtocolContract,
            b: candidate.PeerProtocolContract,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = "peer protocol contract differs between the release pair";
            return false;
        }
        if (
            previous.Definitions.Keys.Except(
            candidate.Definitions.Keys,
            StringComparer.Ordinal
        ).Any() ||
            candidate.Definitions.Keys.Except(
            previous.Definitions.Keys,
            StringComparer.Ordinal
        ).Any()
        ) {
            reason = "release definition inventory changes are not structurally compatible";
            return false;
        }
        if (
            !SameInventory(
            left: previous.DefinitionFiles,
            right: candidate.DefinitionFiles
        ) ||
            !SameInventory(
            left: previous.Artifacts,
            right: candidate.Artifacts
        )
        ) {
            reason = "release artifact inventory or pins differ; engine and metadata transitions cannot change packaged dependencies";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Requires at least one immutable manifest in a metadata pair to exclude older coordinators.
    /// Both manifests are loaded before runtime effects, so this also protects rollback to a legacy manifest.</summary>
    public static bool TryRequireMetadataCoordinator(WorldReleaseManifest source, WorldReleaseManifest target, out string reason) {
        if (
            !WorldReleaseManifest.TryValidate(
            manifest: source,
            reason: out reason
        ) ||
            !WorldReleaseManifest.TryValidate(
            manifest: target,
            reason: out reason
        )
        ) { return false; }
        if (
            (source.CoordinatorContract is not (WorldReleaseManifest.MetadataCoordinatorContract or WorldReleaseManifest.ReceiptCoordinatorContract or WorldReleaseManifest.CurrentCoordinatorContract)) &&
            (target.CoordinatorContract is not (WorldReleaseManifest.MetadataCoordinatorContract or WorldReleaseManifest.ReceiptCoordinatorContract or WorldReleaseManifest.CurrentCoordinatorContract))
        ) {
            reason = "metadata transitions require a package with the metadata coordinator contract; prepare the new release with current tooling";
            return false;
        }
        reason = string.Empty;
        return true;
    }
}
