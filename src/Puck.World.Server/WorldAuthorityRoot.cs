using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Server;

/// <summary>The activation lease a writer must carry when publishing authority state. The lease is deliberately
/// separate from a blob version token: a writer that was paused across a takeover cannot adopt the new lease.</summary>
public readonly record struct WorldAuthorityFence(long Epoch, Guid Token, string RootVersion) {
    /// <summary>The unowned bootstrap lease. It is accepted only while the authority root is still at epoch zero.</summary>
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
    [property: JsonPropertyName("receipt")] string? ReceiptHash,
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
        ReceiptHash: null,
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
);

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
            root = JsonSerializer.Deserialize<WorldAuthorityRoot>(bytes, Options);
            if (root.Version != 1 || root.Epoch < 0 || root.Sequence < 0 || root.CheckpointOrdinal < -1 || root.DurableOrdinal < -1 || root.JournalEntryCount < 0) {
                reason = "root version or monotonic fields are invalid";
                root = default;
                return false;
            }
            reason = string.Empty;
            return true;
        } catch (JsonException error) {
            root = default;
            reason = error.Message.ReplaceLineEndings(" ");
            return false;
        }
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
        } catch (JsonException error) {
            receipt = default;
            previousHash = null;
            reason = error.Message.ReplaceLineEndings(" ");
            return false;
        }
    }

    private sealed record ReceiptNode(
        [property: JsonPropertyName("operation")] Guid OperationId,
        [property: JsonPropertyName("actor")] string Actor,
        [property: JsonPropertyName("payload")] string PayloadDigest,
        [property: JsonPropertyName("decision")] string DecisionCode,
        [property: JsonPropertyName("applied")] bool Applied,
        [property: JsonPropertyName("revision")] long Revision,
        [property: JsonPropertyName("journalOrdinal")] long? CommittedJournalOrdinal,
        [property: JsonPropertyName("previous")] string? PreviousHash
    ) {
        public ReceiptNode(WorldAuthorityOperationReceipt receipt, string? previousHash) : this(receipt.OperationId, receipt.Actor, receipt.PayloadDigest, receipt.DecisionCode, receipt.Applied, receipt.Revision, receipt.CommittedJournalOrdinal, previousHash) { }
    }
}
