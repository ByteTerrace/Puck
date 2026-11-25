using System.Security.Cryptography;
using System.Text.Json;
using Puck.Attestation;

namespace Puck.World;

/// <summary>The startup gate for a <see cref="WorldSiloDefinition"/> document, read once when a silo boots.</summary>
/// <remarks>This validator proves everything a silo document alone can prove: key files parse and exist, no two
/// rows share one, no two rows share an extensions file, no two rows name the same world, and the declared door budget
/// is not exceeded. It cannot prove a
/// row's loaded world definition carries <c>host.authority</c> — that fact depends on the referenced document, not
/// this one, and is refused by name at activation instead. Extension settings are opaque here; the selected installed provider validates them before startup.</remarks>
public static class WorldSiloDefinitionValidator {
    // The same import a host performs when it loads the key, so a file that validates here is exactly one the host
    // accepts: P-256, no trailing bytes.
    private static bool TryValidateKeyFile(string? path, string label, out string reason) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            reason = $"{label} names no keyFile";

            return false;
        }

        if (!File.Exists(path: path)) {
            reason = $"{label} names keyFile '{path}', which does not exist";

            return false;
        }

        try {
            using var key = AttestationKeys.ImportPkcs8PrivateKey(
                algorithm: AttestationAlgorithms.EcdsaP256Sha256,
                pkcs8: File.ReadAllBytes(path: path)
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)) {
            reason = $"{label} keyFile '{path}' could not be read as a PKCS#8 P-256 private key — {exception.Message}";

            return false;
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Validates an issuer configuration: its root key file parses, its sign-in selection names an
    /// extension type and object settings, and its issued-binding lifetime is positive and within the verifier's
    /// ceiling.</summary>
    /// <param name="issuer">The configuration to validate.</param>
    /// <param name="reason">Why validation failed, or empty on success.</param>
    /// <returns><see langword="true"/> when every check holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="issuer"/> is <see langword="null"/>.</exception>
    public static bool TryValidateIssuer(WorldIssuerConfiguration issuer, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: issuer);

        if (!TryValidateKeyFile(path: issuer.KeyFile, label: "issuer", reason: out reason)) {
            return false;
        }
        if (
            (issuer.SignIn is null) ||
            string.IsNullOrWhiteSpace(value: issuer.SignIn.Type) ||
            (issuer.SignIn.Settings.ValueKind != JsonValueKind.Object)
        ) {
            reason = "issuer.signIn requires an extension type and object settings";

            return false;
        }
        if (issuer.SubjectValiditySeconds is < 1 or > WorldIssuerConfiguration.MaximumSubjectValiditySeconds) {
            reason = $"issuer.subjectValiditySeconds {issuer.SubjectValiditySeconds} is outside 1..{WorldIssuerConfiguration.MaximumSubjectValiditySeconds}";

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

        if ((definition.DocumentDirectory is { } documentDirectory) && string.IsNullOrWhiteSpace(value: documentDirectory)) {
            reason = "documentDirectory, when present, names a directory";

            return false;
        }

        if (
            string.IsNullOrWhiteSpace(value: definition.Store.Type) ||
            (definition.Store.Settings.ValueKind != JsonValueKind.Object)
        ) {
            reason = "store requires an extension type and object settings";
            return false;
        }
        if (string.IsNullOrWhiteSpace(value: definition.Clustering.Kind)) {
            reason = "clustering.kind is required";
            return false;
        }
        if (definition.Release is { } release) {
            if (
                string.IsNullOrWhiteSpace(value: release.Group) ||
                release.Group.Any(predicate: char.IsWhiteSpace) ||
                release.Group.Contains(
                comparisonType: StringComparison.Ordinal,
                value: ".."
            ) ||
                release.Group.Contains(value: '/')
            ) {
                reason = "release.group must be one safe deployment-group name";
                return false;
            }
            if (
                (release.Owner == Guid.Empty) ||
                string.IsNullOrWhiteSpace(value: release.ExpectedRelease)
            ) {
                reason = "release requires a non-empty owner and expectedRelease";
                return false;
            }
        }
        if (
            (clusteringKinds is not null) &&
            !clusteringKinds.Contains(
            definition.Clustering.Kind,
            StringComparer.Ordinal
        )
        ) {
            reason = $"clustering.kind '{definition.Clustering.Kind}' is not installed; name one of: {string.Join(
                separator: ", ",
                values: clusteringKinds
            )}";
            return false;
        }
        if (definition.Lifecycle is { } lifecycle) {
            if (
                (lifecycle.ProgressTimeoutSeconds < 1) ||
                (lifecycle.CheckpointTimeoutSeconds < 1) ||
                (lifecycle.JournalTimeoutSeconds < 1) ||
                (lifecycle.JournalBacklogLimit < 1)
            ) {
                reason = "lifecycle health deadlines and journalBacklogLimit must be positive";
                return false;
            }
            if (
                (lifecycle.ShutdownSeconds < 1) ||
                (lifecycle.HealthPort is < 1 or > 65535)
            ) {
                reason = "lifecycle requires a positive shutdownSeconds and valid healthPort";
                return false;
            }
            if (!System.Net.IPAddress.TryParse(
                address: out _,
                ipString: lifecycle.HealthAddress
            )) {
                reason = $"lifecycle.healthAddress '{lifecycle.HealthAddress}' is not an IP address literal";
                return false;
            }
            if (
                (lifecycle.Observer is { } observer) &&
                (string.IsNullOrWhiteSpace(value: observer.Type) || (observer.Settings.ValueKind != JsonValueKind.Object))
            ) {
                reason = "lifecycle.observer requires an extension type and object settings";
                return false;
            }
            if (
                (lifecycle.Telemetry is { } telemetry) &&
                (string.IsNullOrWhiteSpace(value: telemetry.Type) || (telemetry.Settings.ValueKind != JsonValueKind.Object))
            ) {
                reason = "lifecycle.telemetry requires an extension type and object settings";
                return false;
            }
        }
        if (
            (definition.Issuer is { } issuer) &&
            !TryValidateIssuer(issuer: issuer, reason: out reason)
        ) {
            return false;
        }
        var worldIds = new HashSet<string>(comparer: StringComparer.Ordinal);
        var keyFiles = new Dictionary<string, SafeName>(comparer: StringComparer.OrdinalIgnoreCase);
        var extensionFiles = new Dictionary<string, SafeName>(comparer: StringComparer.OrdinalIgnoreCase);
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
            if (!TryValidateKeyFile(
                label: $"world '{world.World}' federation",
                path: world.Federation.KeyFile,
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

            if (
                (definition.Issuer is { } rowIssuer) &&
                string.Equals(a: Path.GetFullPath(path: rowIssuer.KeyFile), b: keyFileFull, comparisonType: StringComparison.OrdinalIgnoreCase)
            ) {
                reason = $"world '{world.World}' signs with the issuer's root key '{world.Federation.KeyFile}' — the root vouches for players and signs as no world";

                return false;
            }

            keyFiles[keyFileFull] = world.World;

            if (world.Extensions is { } extensions) {
                if (string.IsNullOrWhiteSpace(value: extensions)) {
                    reason = $"world '{world.World}' names a blank extensions path";

                    return false;
                }
                var extensionsFull = Path.GetFullPath(path: extensions);

                if (extensionFiles.TryGetValue(
                    key: extensionsFull,
                    value: out var extensionsSharedWith
                )) {
                    reason = $"worlds '{extensionsSharedWith}' and '{world.World}' share the extensions file '{extensions}' — every row's configuration names its own world and lineage";

                    return false;
                }
                extensionFiles[extensionsFull] = world.World;
            }

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
