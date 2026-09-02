using System.Security.Cryptography;
using Puck.Attestation;

namespace Puck.World;

/// <summary>The startup gate for a <see cref="WorldSiloDefinition"/> document, read once when a silo boots.</summary>
/// <remarks>This validator proves everything a silo document alone can prove: key files parse and exist, no two
/// rows share one, no two rows name the same world, and the declared door budget is not exceeded. It cannot prove a
/// row's loaded world definition carries <c>host.authority</c> — that fact depends on the referenced document, not
/// this one, and is refused by name at activation instead.</remarks>
public static class WorldSiloDefinitionValidator {
    private static bool TryValidateClustering(WorldSiloClustering clustering, out string reason) {
        switch (clustering.Kind) {
            case WorldSiloClusteringKind.Localhost:
                if (clustering.TableName is { Length: > 0 }) {
                    reason = "clustering.kind is 'localhost' but clustering.tableName is set";

                    return false;
                }

                break;
            case WorldSiloClusteringKind.Table:
                if (string.IsNullOrWhiteSpace(value: clustering.TableName)) {
                    reason = "clustering.kind is 'table' but clustering.tableName is missing";

                    return false;
                }

                break;
            default:
                reason = $"clustering.kind '{clustering.Kind}' is not recognized";

                return false;
        }

        reason = string.Empty;

        return true;
    }
    private static bool TryValidateFederationKey(WorldSafeName world, WorldSiloFederation federation, out string reason) {
        if (string.IsNullOrWhiteSpace(value: federation.KeyFile)) {
            reason = $"world '{world}' names no federation.keyFile";

            return false;
        }

        if (!File.Exists(path: federation.KeyFile)) {
            reason = $"world '{world}' names federation.keyFile '{federation.KeyFile}', which does not exist";

            return false;
        }

        try {
            var pkcs8 = File.ReadAllBytes(path: federation.KeyFile);
            // The same import the silo host performs when it loads the row, so a key file that validates here is
            // exactly one the host will accept: P-256, no trailing bytes.
            using var key = AttestationKeys.ImportPkcs8PrivateKey(
                algorithm: AttestationAlgorithms.EcdsaP256Sha256,
                pkcs8: pkcs8
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)) {
            reason = $"world '{world}' federation.keyFile '{federation.KeyFile}' could not be read as a PKCS#8 P-256 private key — {exception.Message}";

            return false;
        }

        reason = string.Empty;

        return true;
    }
    private static bool TryValidateStore(WorldSiloStore store, out string reason) {
        switch (store.Kind) {
            case WorldSiloStoreKind.Directory:
                if (string.IsNullOrWhiteSpace(value: store.DirectoryPath)) {
                    reason = "store.kind is 'directory' but store.directoryPath is missing";

                    return false;
                }

                if (store.AccountUrl is { Length: > 0 }) {
                    reason = "store.kind is 'directory' but store.accountUrl is set";

                    return false;
                }

                break;
            case WorldSiloStoreKind.Azure:
                if (string.IsNullOrWhiteSpace(value: store.AccountUrl)) {
                    reason = "store.kind is 'azure' but store.accountUrl is missing";

                    return false;
                }

                if (store.DirectoryPath is { Length: > 0 }) {
                    reason = "store.kind is 'azure' but store.directoryPath is set";

                    return false;
                }

                break;
            default:
                reason = $"store.kind '{store.Kind}' is not recognized";

                return false;
        }

        reason = string.Empty;

        return true;
    }

    /// <summary>Validates a silo document.</summary>
    /// <param name="definition">The document to validate.</param>
    /// <param name="reason">Why validation failed, naming the offending row, or empty on success.</param>
    /// <returns><see langword="true"/> when every check holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryValidate(WorldSiloDefinition definition, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!string.Equals(
            a: definition.Schema,
            b: WorldSiloDefinition.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"schema '{definition.Schema}' is not '{WorldSiloDefinition.SchemaVersion}'";

            return false;
        }

        if (definition.Doors.Budget < 0) {
            reason = $"doors.budget {definition.Doors.Budget} is negative";

            return false;
        }

        if (string.IsNullOrWhiteSpace(value: definition.StateDir)) {
            reason = "stateDir is required";

            return false;
        }

        if (!TryValidateStore(
            store: definition.Store,
            reason: out reason
        )) {
            return false;
        }

        if (!TryValidateClustering(
            clustering: definition.Clustering,
            reason: out reason
        )) {
            return false;
        }

        var worldIds = new HashSet<string>(comparer: StringComparer.Ordinal);
        var keyFiles = new Dictionary<string, WorldSafeName>(comparer: StringComparer.OrdinalIgnoreCase);
        var pinnedCount = 0;

        foreach (var world in definition.Worlds) {
            if (world.Owner == Guid.Empty) {
                reason = $"world '{world.World}' names an empty owner oid";

                return false;
            }

            if (!worldIds.Add(item: world.World.Value)) {
                reason = $"world id '{world.World}' is declared more than once";

                return false;
            }

            if (!TryValidateFederationKey(
                world: world.World,
                federation: world.Federation,
                reason: out reason
            )) {
                return false;
            }

            var keyFileFull = Path.GetFullPath(path: world.Federation.KeyFile);

            if (keyFiles.TryGetValue(
                key: keyFileFull,
                value: out var sharedWith
            )) {
                reason = $"worlds '{sharedWith}' and '{world.World}' share the key file '{world.Federation.KeyFile}' — every world signs under its own key";

                return false;
            }

            keyFiles[keyFileFull] = world.World;

            if (world.Pinned) {
                pinnedCount++;
            }
        }

        if (pinnedCount > definition.Doors.Budget) {
            reason = $"{pinnedCount} pinned world(s) exceed the declared doors.budget of {definition.Doors.Budget}";

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
