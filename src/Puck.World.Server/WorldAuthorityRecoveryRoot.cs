using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Server;

/// <summary>A content-addressed, immutable authority-root snapshot retained as a pre-cutover recovery point.
/// The pin is safe to persist in a deployment-group record; resolving it always rechecks the owner, world, and
/// release operation carried by the snapshot.</summary>
/// <param name="Pin">The full SHA-256 content pin of the immutable snapshot blob.</param>
/// <param name="Owner">The owner bound into the snapshot.</param>
/// <param name="World">The world bound into the snapshot.</param>
/// <param name="OperationId">The release operation that captured the snapshot.</param>
/// <param name="Root">The complete persisted authority root, including all immutable payload references.</param>
/// <param name="CapturedRootVersion">The object-store version token observed when the snapshot was captured.</param>
/// <param name="CapturedAt">The UTC time the recovery point was first persisted.</param>
public readonly record struct WorldRecoveryRootReference(
    string Pin,
    Guid Owner,
    SafeName World,
    Guid OperationId,
    WorldAuthorityRoot Root,
    string CapturedRootVersion,
    DateTimeOffset CapturedAt
);
/// <summary>Optional recovery-root persistence implemented by the directory and cloud authority store. Keeping this
/// as a capability lets existing authority-store test doubles remain small while managed hosting requires the real
/// immutable snapshot implementation.</summary>
public interface IWorldAuthorityRecoveryStore {
    /// <summary>Captures the current complete authority root as an immutable, operation-bound snapshot.</summary>
    Task<WorldRecoveryRootReference?> CaptureRecoveryRootAsync(
        WorldAuthorityIdentity identity,
        Guid operationId,
        CancellationToken cancellationToken
    );
    /// <summary>Resolves and validates one operation-bound recovery snapshot.</summary>
    Task<WorldRecoveryRootReference?> LoadRecoveryRootAsync(
        WorldAuthorityIdentity identity,
        string pin,
        Guid operationId,
        CancellationToken cancellationToken
    );
    /// <summary>Restores immutable payload references from a captured snapshot under the caller's still-current
    /// activation fence. The restored root is unowned and carries a fresh epoch and sequence, so a subsequent source
    /// activation must acquire a new fence.</summary>
    Task<WorldAuthorityStoreOutcome> RestoreRecoveryRootAsync(
        WorldAuthorityIdentity identity,
        string pin,
        Guid operationId,
        WorldAuthorityFence expectedCurrentFence,
        CancellationToken cancellationToken
    );
}

internal static class WorldAuthorityRecoveryRootCodec {
    private const string HashPrefix = "sha256/";
    private const string Schema = "puck.authority.recovery-root.v1";

    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    private sealed record Envelope(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("owner")] Guid Owner,
        [property: JsonPropertyName("world")] string World,
        [property: JsonPropertyName("operation")] Guid OperationId,
        [property: JsonPropertyName("rootVersion")] string RootVersion,
        [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
        [property: JsonPropertyName("root")] WorldAuthorityRoot Root
    );

    public static string ComputePin(ReadOnlySpan<byte> bytes) {
        var digest = System.Security.Cryptography.SHA256.HashData(source: bytes);

        return (HashPrefix + Convert.ToHexStringLower(inArray: digest));
    }
    public static byte[] Encode(WorldAuthorityIdentity identity, Guid operationId, WorldAuthorityRootSnapshot root) => JsonSerializer.SerializeToUtf8Bytes(
        new Envelope(
            Schema,
            identity.Owner,
            identity.World.Value,
            operationId,
            root.VersionToken,
            DateTimeOffset.UtcNow,
            root.Root
        ),
        Options
    );
    public static bool IsPin(string? pin) {
        if (
            (pin is null) ||
            (pin.Length != (HashPrefix.Length + 64)) ||
            !pin.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: HashPrefix
        )
        ) {
            return false;
        }
        for (var index = HashPrefix.Length; (index < pin.Length); index++) {
            var character = pin[index];

            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f')) {
                return false;
            }
        }
        return true;
    }
    public static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        WorldAuthorityIdentity expectedIdentity,
        Guid expectedOperationId,
        out WorldRecoveryRootReference reference,
        out string reason
    ) {
        reference = default;
        try {
            using var document = JsonDocument.Parse(bytes.ToArray());

            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                reason = "recovery root must be a JSON object";
                return false;
            }

            var allowed = new HashSet<string>(comparer: StringComparer.Ordinal) { "schema", "owner", "world", "operation", "rootVersion", "capturedAt", "root" };
            var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject()) {
                if (!seen.Add(item: property.Name)) {
                    reason = $"duplicate recovery-root member '{property.Name}'";
                    return false;
                }
                if (!allowed.Contains(item: property.Name)) {
                    reason = $"unknown recovery-root member '{property.Name}'";
                    return false;
                }
            }
            foreach (var name in allowed) {
                if (!seen.Contains(item: name)) {
                    reason = $"recovery-root member '{name}' is missing";
                    return false;
                }
            }

            var envelope = JsonSerializer.Deserialize<Envelope>(
                options: Options,
                utf8Json: bytes
            );

            if (
                (envelope is null) ||
                !string.Equals(
                a: envelope.Schema,
                b: Schema,
                comparisonType: StringComparison.Ordinal
            ) ||
                (envelope.Owner == Guid.Empty) ||
                (envelope.OperationId == Guid.Empty) ||
                string.IsNullOrWhiteSpace(value: envelope.World) ||
                string.IsNullOrWhiteSpace(value: envelope.RootVersion) ||
                (envelope.CapturedAt == default)
            ) {
                reason = "recovery-root fields are incomplete";
                return false;
            }
            SafeName world;

            try {
                world = SafeName.Parse(candidate: envelope.World);
            } catch (Exception error) when ((error is ArgumentException or FormatException)) {
                reason = "recovery-root world is invalid";
                return false;
            }
            if (
                (envelope.Owner != expectedIdentity.Owner) ||
                (world != expectedIdentity.World) ||
                (envelope.OperationId != expectedOperationId)
            ) {
                reason = "recovery-root identity or operation does not match the requested restore";
                return false;
            }

            var rootElement = document.RootElement.GetProperty(propertyName: "root");
            var rootReason = string.Empty;

            if (
                (rootElement.ValueKind != JsonValueKind.Object) ||
                !WorldAuthorityRootCodec.TryDecode(
                Encoding.UTF8.GetBytes(s: rootElement.GetRawText()),
                out var root,
                out rootReason
            )
            ) {
                reason = $"recovery-root authority root is corrupt — {rootReason}";
                return false;
            }
            reference = new WorldRecoveryRootReference(
                Pin: ComputePin(bytes: bytes),
                Owner: envelope.Owner,
                World: world,
                OperationId: envelope.OperationId,
                Root: root,
                CapturedRootVersion: envelope.RootVersion,
                CapturedAt: envelope.CapturedAt
            );
            reason = string.Empty;
            return true;
        } catch (Exception error) when ((error is JsonException or InvalidOperationException or FormatException or ArgumentException)) {
            reason = error.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
    }
}
