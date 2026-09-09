using System.Security.Cryptography;
using System.Text.Json;
using Puck.Attestation;

namespace Puck.World;

/// <summary>The startup gate for a <see cref="WorldSiloDefinition"/> document, read once when a silo boots.</summary>
/// <remarks>This validator proves everything a silo document alone can prove: key files parse and exist, no two
/// rows share one, no two rows name the same world, and the declared door budget is not exceeded. It cannot prove a
/// row's loaded world definition carries <c>host.authority</c> — that fact depends on the referenced document, not
/// this one, and is refused by name at activation instead. Extension settings are opaque here; the selected installed provider validates them before startup.</remarks>
public static class WorldSiloDefinitionValidator {
    private static bool TryValidateFederationKey(SafeName world, WorldSiloFederation federation, out string reason) {
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

    /// <summary>Validates a silo document.</summary>
    /// <param name="definition">The document to validate.</param>
    /// <param name="reason">Why validation failed, naming the offending row, or empty on success.</param>
    /// <param name="clusteringKinds">Installed clustering keys from the host registry; absent for structural validation only.</param>
    /// <returns><see langword="true"/> when every check holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryValidate(WorldSiloDefinition definition, out string reason, IReadOnlyCollection<string>? clusteringKinds = null) {
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

        if (string.IsNullOrWhiteSpace(value: definition.Store.Type) || (definition.Store.Settings.ValueKind != JsonValueKind.Object)) {
            reason = "store requires an extension type and object settings";
            return false;
        }
        if (string.IsNullOrWhiteSpace(value: definition.Clustering.Kind)) {
            reason = "clustering.kind is required";
            return false;
        }
        if ((clusteringKinds is not null) && !clusteringKinds.Contains(definition.Clustering.Kind, StringComparer.Ordinal)) {
            reason = $"clustering.kind '{definition.Clustering.Kind}' is not installed; name one of: {string.Join(separator: ", ", values: clusteringKinds)}";
            return false;
        }
        if (definition.Lifecycle is { } lifecycle) {
            if ((lifecycle.ProgressTimeoutSeconds < 1) || (lifecycle.CheckpointTimeoutSeconds < 1) || (lifecycle.JournalTimeoutSeconds < 1) || (lifecycle.JournalBacklogLimit < 1)) {
                reason = "lifecycle health deadlines and journalBacklogLimit must be positive";
                return false;
            }
            if ((lifecycle.ShutdownSeconds < 1) || (lifecycle.HealthPort is < 1 or > 65535)) {
                reason = "lifecycle requires a positive shutdownSeconds and valid healthPort";
                return false;
            }
            if ((lifecycle.Observer is { } observer) && (string.IsNullOrWhiteSpace(value: observer.Type) || (observer.Settings.ValueKind != JsonValueKind.Object))) {
                reason = "lifecycle.observer requires an extension type and object settings";
                return false;
            }
        }
        var worldIds = new HashSet<string>(comparer: StringComparer.Ordinal);
        var keyFiles = new Dictionary<string, SafeName>(comparer: StringComparer.OrdinalIgnoreCase);
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
            if (world.Federation.Authentication is { } authentication &&
                (string.IsNullOrWhiteSpace(authentication.Type) || authentication.Settings.ValueKind != JsonValueKind.Object)) {
                reason = $"world '{world.World}' authentication requires an extension type and object settings";
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
