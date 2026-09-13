using System.Text.Json;

namespace Puck.World.Server;

/// <summary>Exact receipt index and chain bytes selected by one authority root. The source root is provenance;
/// restoring this object must never install its production activation fence in a disposable fixture.</summary>
/// <param name="Owner">The source world's owner.</param>
/// <param name="World">The source world's stable name.</param>
/// <param name="Source">The root selected at the checkpoint capture boundary.</param>
/// <param name="Index">The original index bytes, empty when the root has no receipts.</param>
/// <param name="Nodes">Original receipt bytes keyed by their existing content pins.</param>
public sealed record WorldAuthorityReceiptSnapshot(Guid Owner, string World, WorldAuthorityRootSnapshot Source,
    byte[] Index, IReadOnlyDictionary<string, byte[]> Nodes) {
    /// <summary>The original receipt snapshot wire contract.</summary>
    public const string CurrentSchema = "puck.world.receipts.v1";
    /// <summary>The encoded snapshot's contract.</summary>
    public string Schema { get; init; } = CurrentSchema;
    /// <summary>The maximum number of receipts accepted in one export.</summary>
    public const int MaximumReceipts = 100_000;
    /// <summary>The combined raw-byte budget for the index and receipt nodes.</summary>
    public const int MaximumBytes = 64 * 1024 * 1024;
    /// <summary>The encoded envelope budget, including base64 expansion and provenance.</summary>
    public const int MaximumEncodedBytes = 96 * 1024 * 1024;

    /// <summary>Encodes a validated snapshot in stable node order without changing its original payload bytes.</summary>
    public byte[] Encode() {
        _ = Validate();
        var encoded = JsonSerializer.SerializeToUtf8Bytes(this with { Nodes = new SortedDictionary<string, byte[]>(Nodes.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal) });
        if (encoded.Length > MaximumEncodedBytes) { throw new InvalidDataException("receipt snapshot envelope exceeds its byte budget"); }
        return encoded;
    }

    /// <summary>Reads a bounded canonical receipt snapshot and validates its complete graph.</summary>
    /// <param name="bytes">The exact encoded snapshot.</param>
    /// <returns>The validated snapshot with detached payload bytes.</returns>
    /// <exception cref="InvalidDataException">The snapshot is unsupported, malformed, noncanonical or oversized.</exception>
    public static WorldAuthorityReceiptSnapshot Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.Length is 0 or > MaximumEncodedBytes) { throw new InvalidDataException("receipt snapshot envelope exceeds its byte budget"); }
        try {
            var snapshot = JsonSerializer.Deserialize<WorldAuthorityReceiptSnapshot>(bytes) ?? throw new InvalidDataException("receipt snapshot is empty");
            if (!bytes.SequenceEqual(snapshot.Encode())) { throw new InvalidDataException("receipt snapshot is not canonical"); }
            return snapshot;
        } catch (JsonException error) { throw new InvalidDataException("receipt snapshot envelope is malformed", error); }
    }

    /// <summary>Verifies the original pins, complete chain/index agreement, ownership and budgets without
    /// changing or recanonicalizing receipt bytes. Returns every operation for independent lookup checks.</summary>
    /// <returns>The validated operation inventory.</returns>
    /// <exception cref="InvalidDataException">The snapshot is incomplete, corrupt, inconsistent or oversized.</exception>
    public IReadOnlyDictionary<Guid, WorldAuthorityOperationReceipt> Validate() {
        if (Schema != CurrentSchema) { throw new InvalidDataException($"unsupported receipt snapshot schema '{Schema}'"); }
        if (Owner == Guid.Empty || string.IsNullOrWhiteSpace(World) || string.IsNullOrWhiteSpace(Source.VersionToken) || Source.VersionToken.Length > 4096 ||
            Index is null || Nodes is null || Nodes.Count > MaximumReceipts ||
            Index.LongLength + Nodes.Values.Sum(bytes => bytes?.LongLength ?? MaximumBytes + 1L) > MaximumBytes) {
            throw new InvalidDataException("receipt snapshot has invalid identity, inventory or byte budget");
        }
        if (!SafeName.TryParse(World, out _, out var worldReason)) { throw new InvalidDataException("receipt snapshot world is invalid: " + worldReason); }
        if (!WorldAuthorityRootCodec.TryDecode(WorldAuthorityRootCodec.Encode(Source.Root), out _, out var reason)) {
            throw new InvalidDataException("receipt snapshot source root is invalid: " + reason);
        }
        var receipts = new Dictionary<Guid, WorldAuthorityOperationReceipt>();
        if (Source.Root.ReceiptIndexHash is null) {
            if (Index.Length != 0 || Nodes.Count != 0) { throw new InvalidDataException("receipt snapshot has payloads without root references"); }
            return receipts;
        }
        if (WorldDefinitionFileSource.ComputeContentHash(Index) != Source.Root.ReceiptIndexHash) {
            throw new InvalidDataException("receipt snapshot index does not match its source pin");
        }
        try {
            using var document = JsonDocument.Parse(Index);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("receipt snapshot index must be an object"); }
            var index = new Dictionary<Guid, string>();
            foreach (var member in document.RootElement.EnumerateObject()) {
                if (!Guid.TryParse(member.Name, out var operation) || operation == Guid.Empty || member.Value.ValueKind != JsonValueKind.String ||
                    !index.TryAdd(operation, member.Value.GetString()!) || index.Count > MaximumReceipts) {
                    throw new InvalidDataException("receipt snapshot index has invalid or duplicate operations");
                }
            }
            if (index.Count != Nodes.Count) { throw new InvalidDataException("receipt snapshot index and node inventory differ"); }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pin = Source.Root.ReceiptHash;
            while (pin is not null) {
                if (!visited.Add(pin) || !Nodes.TryGetValue(pin, out var bytes) || WorldDefinitionFileSource.ComputeContentHash(bytes) != pin) {
                    throw new InvalidDataException("receipt snapshot chain is missing, cyclic or corrupt");
                }
                if (!WorldAuthorityRootCodec.TryDecodeReceipt(bytes, out var receipt, out var previous, out reason) ||
                    !index.TryGetValue(receipt.OperationId, out var indexed) || indexed != pin || !receipts.TryAdd(receipt.OperationId, receipt)) {
                    throw new InvalidDataException("receipt snapshot chain and operation index disagree");
                }
                pin = previous;
            }
            if (visited.Count != Nodes.Count) { throw new InvalidDataException("receipt snapshot contains nodes outside its source chain"); }
            return receipts;
        } catch (JsonException error) { throw new InvalidDataException("receipt snapshot index is malformed", error); }
    }
}
