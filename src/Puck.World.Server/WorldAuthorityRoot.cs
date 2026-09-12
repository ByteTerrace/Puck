using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Server;

/// <summary>The activation lease a writer must carry when publishing authority state. The lease is deliberately
/// separate from a blob version token: a writer that was paused across a takeover cannot adopt the new lease.</summary>
public readonly record struct WorldAuthorityFence(long Epoch, Guid Token, string RootVersion) {
    /// <summary>The unowned bootstrap/offline-publication lease. It is accepted only when the published root carries
    /// no active fence (epoch zero during bootstrap or an empty-token released epoch).</summary>
    public static WorldAuthorityFence Unowned { get; } = new(0, Guid.Empty, string.Empty);
}

/// <summary>The immutable references and monotonic sequence read from one authority root.</summary>
public readonly record struct WorldAuthorityRoot(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("epoch")] long Epoch,
    [property: JsonPropertyName("fence")] Guid FenceToken,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("definition")] string? DefinitionHash,
    [property: JsonPropertyName("checkpoint")] string? CheckpointHash,
    [property: JsonPropertyName("checkpointOrdinal")] long CheckpointOrdinal,
    [property: JsonPropertyName("checkpointTick")] ulong CheckpointTick,
    [property: JsonPropertyName("journal")] string? JournalHash,
    [property: JsonPropertyName("journalCount")] int JournalEntryCount,
    [property: JsonPropertyName("journalSequence")] long JournalSequence,
    [property: JsonPropertyName("checkpointCoverageSequence")] long CheckpointCoverageSequence,
    [property: JsonPropertyName("receipt")] string? ReceiptHash,
    [property: JsonPropertyName("receiptIndex")] string? ReceiptIndexHash,
    [property: JsonPropertyName("durableOrdinal")] long DurableOrdinal,
    [property: JsonPropertyName("durableTick")] ulong DurableTick
) {
    /// <summary>Creates an empty, unowned root used by explicit legacy initialization.</summary>
    public static WorldAuthorityRoot Empty => new(
        Version: 1,
        Epoch: 0,
        FenceToken: Guid.Empty,
        Sequence: 0,
        DefinitionHash: null,
        CheckpointHash: null,
        CheckpointOrdinal: -1,
        CheckpointTick: 0,
        JournalHash: null,
        JournalEntryCount: 0,
        JournalSequence: -1,
        CheckpointCoverageSequence: -1,
        ReceiptHash: null,
        ReceiptIndexHash: null,
        DurableOrdinal: -1,
        DurableTick: 0
    );
}

/// <summary>A root together with the blob version token that must guard its next CAS.</summary>
public readonly record struct WorldAuthorityRootSnapshot(WorldAuthorityRoot Root, string VersionToken);

/// <summary>A coherent recovery view. The root was read once and all referenced immutable blobs were verified against
/// that root, so checkpoint and journal cannot come from different publications.</summary>
public readonly record struct WorldAuthorityRecovery(
    WorldAuthorityRootSnapshot Root,
    WorldAuthorityCheckpointBlob? Checkpoint,
    WorldMutationJournalTail Journal
) {
    /// <summary>The root-qualified, fully validated definition, or <see langword="null"/> when no definition is published.</summary>
    public WorldDefinition? Definition { get; init; }
}

/// <summary>Server-neutral durable receipt facts. Decision vocabulary remains a stable code so Protocol can evolve
/// its typed outcome without creating a Server-to-Protocol persistence cycle.</summary>
public readonly record struct WorldAuthorityOperationReceipt(
    Guid OperationId,
    string Actor,
    string PayloadDigest,
    string DecisionCode,
    bool Applied,
    long Revision,
    long? CommittedJournalOrdinal
);

/// <summary>Encodes the additive mutable authority root and immutable receipt nodes. Existing checkpoint, journal, and
/// definition payloads are passed through unchanged.</summary>
internal static class WorldAuthorityRootCodec {
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    public static byte[] Encode(WorldAuthorityRoot root) => JsonSerializer.SerializeToUtf8Bytes(root, Options);

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out WorldAuthorityRoot root, out string reason) {
        try {
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                root = default;
                reason = "authority root must be a JSON object";
                return false;
            }
            var allowed = new HashSet<string>(StringComparer.Ordinal) {
                "version", "epoch", "fence", "sequence", "definition", "checkpoint", "checkpointOrdinal",
                "checkpointTick", "journal", "journalCount", "journalSequence", "checkpointCoverageSequence",
                "receipt", "receiptIndex", "durableOrdinal", "durableTick"
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) {
                if (!seen.Add(property.Name)) {
                    root = default;
                    reason = $"duplicate root member '{property.Name}'";
                    return false;
                }
                if (!allowed.Contains(property.Name)) {
                    root = default;
                    reason = $"unknown root member '{property.Name}'";
                    return false;
                }
            }
            var required = new[] { "version", "epoch", "fence", "sequence", "checkpointOrdinal", "checkpointTick", "journalCount", "journalSequence", "checkpointCoverageSequence", "receiptIndex", "durableOrdinal", "durableTick" };
            foreach (var name in required) {
                if (!document.RootElement.TryGetProperty(name, out _)) {
                    root = default;
                    reason = $"root member '{name}' is missing";
                    return false;
                }
            }
            root = JsonSerializer.Deserialize<WorldAuthorityRoot>(bytes, Options);
            var scalarFieldsValid = root.Version == 1 && root.Epoch >= 0 && root.Sequence >= 0 && root.CheckpointOrdinal >= -1 && root.DurableOrdinal >= -1 && root.JournalEntryCount >= 0 && root.JournalSequence >= -1 && root.CheckpointCoverageSequence >= -1;
            var journalCoverageValid = scalarFieldsValid && root.CheckpointCoverageSequence <= root.JournalSequence &&
                (root.JournalHash is null
                    ? root.JournalEntryCount == 0 && root.JournalSequence == root.CheckpointCoverageSequence
                    : root.CheckpointCoverageSequence == -1
                        ? root.JournalSequence == root.JournalEntryCount - 1L
                        : root.JournalSequence - root.CheckpointCoverageSequence == root.JournalEntryCount);
            if (!journalCoverageValid) {
                reason = "root version or monotonic fields are invalid";
                root = default;
                return false;
            }
            if (root.Epoch == 0 && root.FenceToken != Guid.Empty) { reason = "epoch zero carries a fence"; root = default; return false; }
            if ((root.CheckpointHash is null) != (root.CheckpointOrdinal < 0)) { reason = "checkpoint hash and ordinal disagree"; root = default; return false; }
            if ((root.JournalHash is null) != (root.JournalEntryCount == 0)) { reason = "journal hash and count disagree"; root = default; return false; }
            if ((root.ReceiptHash is null) != (root.ReceiptIndexHash is null)) { reason = "receipt chain and index disagree"; root = default; return false; }
            if (!IsContentPin(root.DefinitionHash) || !IsContentPin(root.CheckpointHash) || !IsContentPin(root.JournalHash) || !IsContentPin(root.ReceiptHash) || !IsContentPin(root.ReceiptIndexHash)) { reason = "root content reference is not a canonical sha256-64 pin"; root = default; return false; }
            reason = string.Empty;
            return true;
        } catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) {
            root = default;
            reason = error.Message.ReplaceLineEndings(" ");
            return false;
        }
    }
    private static bool IsContentPin(string? value) {
        const string prefix = "sha256-64/";
        if (value is null) {
            return true;
        }
        if (value.Length != prefix.Length + 16 || !value.StartsWith(prefix, StringComparison.Ordinal)) {
            return false;
        }
        for (var index = prefix.Length; index < value.Length; index++) {
            if (!Uri.IsHexDigit(value[index])) {
                return false;
            }
        }
        return true;
    }

    public static byte[] EncodeReceipt(WorldAuthorityOperationReceipt receipt, string? previousHash) {
        return JsonSerializer.SerializeToUtf8Bytes(new ReceiptNode(receipt, previousHash), Options);
    }

    public static bool TryDecodeReceipt(ReadOnlySpan<byte> bytes, out WorldAuthorityOperationReceipt receipt, out string? previousHash, out string reason) {
        try {
            var node = JsonSerializer.Deserialize<ReceiptNode>(bytes, Options);
            if (node is null || node.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(node.Actor) || string.IsNullOrWhiteSpace(node.PayloadDigest) || string.IsNullOrWhiteSpace(node.DecisionCode)) {
                receipt = default;
                previousHash = null;
                reason = "receipt fields are incomplete";
                return false;
            }
            receipt = new WorldAuthorityOperationReceipt(node.OperationId, node.Actor, node.PayloadDigest, node.DecisionCode, node.Applied, node.Revision, node.CommittedJournalOrdinal);
            previousHash = node.PreviousHash;
            reason = string.Empty;
            return true;
        } catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) {
            receipt = default;
            previousHash = null;
            reason = error.Message.ReplaceLineEndings(" ");
            return false;
        }
    }

    private sealed record ReceiptNode {
        [JsonPropertyName("operation")] public Guid OperationId { get; init; }
        [JsonPropertyName("actor")] public string Actor { get; init; } = string.Empty;
        [JsonPropertyName("payload")] public string PayloadDigest { get; init; } = string.Empty;
        [JsonPropertyName("decision")] public string DecisionCode { get; init; } = string.Empty;
        [JsonPropertyName("applied")] public bool Applied { get; init; }
        [JsonPropertyName("revision")] public long Revision { get; init; }
        [JsonPropertyName("journalOrdinal")] public long? CommittedJournalOrdinal { get; init; }
        [JsonPropertyName("previous")] public string? PreviousHash { get; init; }

        public ReceiptNode() { }
        public ReceiptNode(WorldAuthorityOperationReceipt receipt, string? previousHash) {
            OperationId = receipt.OperationId;
            Actor = receipt.Actor;
            PayloadDigest = receipt.PayloadDigest;
            DecisionCode = receipt.DecisionCode;
            Applied = receipt.Applied;
            Revision = receipt.Revision;
            CommittedJournalOrdinal = receipt.CommittedJournalOrdinal;
            PreviousHash = previousHash;
        }
    }
}
