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
    /// <summary>The combined raw-byte budget for the index and receipt nodes.</summary>
    public const int MaximumBytes = ((64 * 1024) * 1024);
    /// <summary>The encoded envelope budget, including base64 expansion and provenance.</summary>
    public const int MaximumEncodedBytes = ((96 * 1024) * 1024);
    /// <summary>The maximum number of receipts accepted in one export.</summary>
    public const int MaximumReceipts = 100_000;

    /// <summary>The encoded snapshot's contract.</summary>
    public string Schema { get; init; } = CurrentSchema;

    /// <summary>Reads a bounded canonical receipt snapshot and validates its complete graph.</summary>
    /// <param name="bytes">The exact encoded snapshot.</param>
    /// <returns>The validated snapshot with detached payload bytes.</returns>
    /// <exception cref="InvalidDataException">The snapshot is unsupported, malformed, noncanonical or oversized.</exception>
    public static WorldAuthorityReceiptSnapshot Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.Length is 0 or > MaximumEncodedBytes) { throw new InvalidDataException(message: "receipt snapshot envelope exceeds its byte budget"); }
        try {
            var snapshot = (JsonSerializer.Deserialize<WorldAuthorityReceiptSnapshot>(bytes) ?? throw new InvalidDataException(message: "receipt snapshot is empty"));

            if (!bytes.SequenceEqual(other: snapshot.Encode())) { throw new InvalidDataException(message: "receipt snapshot is not canonical"); }
            return snapshot;
        } catch (JsonException error) {
            throw new InvalidDataException(
            innerException: error,
            message: "receipt snapshot envelope is malformed"
        );
        }
    }
    /// <summary>Encodes a validated snapshot in stable node order without changing its original payload bytes.</summary>
    public byte[] Encode() {
        _ = Validate();
        var encoded = JsonSerializer.SerializeToUtf8Bytes(this with {
            Nodes = new SortedDictionary<string, byte[]>(
            Nodes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
            ),
            StringComparer.Ordinal
        ),
        });

        if (encoded.Length > MaximumEncodedBytes) { throw new InvalidDataException(message: "receipt snapshot envelope exceeds its byte budget"); }
        return encoded;
    }
    /// <summary>Verifies the original pins, complete chain/index agreement, ownership and budgets without
    /// changing or recanonicalizing receipt bytes. Returns every operation for independent lookup checks.</summary>
    /// <returns>The validated operation inventory.</returns>
    /// <exception cref="InvalidDataException">The snapshot is incomplete, corrupt, inconsistent or oversized.</exception>
    public IReadOnlyDictionary<Guid, WorldAuthorityOperationReceipt> Validate() {
        if (Schema != CurrentSchema) { throw new InvalidDataException(message: $"unsupported receipt snapshot schema '{Schema}'"); }
        if (
            (Owner == Guid.Empty) ||
            string.IsNullOrWhiteSpace(value: World) ||
            string.IsNullOrWhiteSpace(value: Source.VersionToken) ||
            (Source.VersionToken.Length > 4096) ||
            (Index is null) ||
            (Nodes is null) ||
            (Nodes.Count > MaximumReceipts) ||
            ((Index.LongLength + Nodes.Values.Sum(selector: bytes => (bytes?.LongLength ?? (MaximumBytes + 1L)))) > MaximumBytes)
        ) {
            throw new InvalidDataException(message: "receipt snapshot has invalid identity, inventory or byte budget");
        }
        if (!SafeName.TryParse(
            candidate: World,
            name: out _,
            reason: out var worldReason
        )) { throw new InvalidDataException(message: ("receipt snapshot world is invalid: " + worldReason)); }
        if (!WorldAuthorityRootCodec.TryDecode(
            WorldAuthorityRootCodec.Encode(root: Source.Root),
            out _,
            out var reason
        )) {
            throw new InvalidDataException(message: ("receipt snapshot source root is invalid: " + reason));
        }
        var receipts = new Dictionary<Guid, WorldAuthorityOperationReceipt>();

        if (Source.Root.ReceiptIndexHash is null) {
            if (
                (Index.Length != 0) ||
                (Nodes.Count != 0)
            ) { throw new InvalidDataException(message: "receipt snapshot has payloads without root references"); }
            return receipts;
        }
        if (WorldDefinitionFileSource.ComputeContentHash(content: Index) != Source.Root.ReceiptIndexHash) {
            throw new InvalidDataException(message: "receipt snapshot index does not match its source pin");
        }
        try {
            using var document = JsonDocument.Parse(Index);

            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(message: "receipt snapshot index must be an object"); }
            var index = new Dictionary<Guid, string>();

            foreach (var member in document.RootElement.EnumerateObject()) {
                if (
                    !Guid.TryParse(
                    input: member.Name,
                    result: out var operation
                ) ||
                    (operation == Guid.Empty) ||
                    (member.Value.ValueKind != JsonValueKind.String) ||
                    !index.TryAdd(
                    key: operation,
                    value: member.Value.GetString()!
                ) ||
                    (index.Count > MaximumReceipts)
                ) {
                    throw new InvalidDataException(message: "receipt snapshot index has invalid or duplicate operations");
                }
            }
            if (index.Count != Nodes.Count) { throw new InvalidDataException(message: "receipt snapshot index and node inventory differ"); }
            var visited = new HashSet<string>(comparer: StringComparer.Ordinal);
            var pin = Source.Root.ReceiptHash;

            while (pin is not null) {
                if (
                    !visited.Add(item: pin) ||
                    !Nodes.TryGetValue(
                    key: pin,
                    value: out var bytes
                ) ||
                    (WorldDefinitionFileSource.ComputeContentHash(content: bytes) != pin)
                ) {
                    throw new InvalidDataException(message: "receipt snapshot chain is missing, cyclic or corrupt");
                }
                if (
                    !WorldAuthorityRootCodec.TryDecodeReceipt(
                    bytes: bytes,
                    previousHash: out var previous,
                    reason: out reason,
                    receipt: out var receipt
                ) ||
                    !index.TryGetValue(
                    key: receipt.OperationId,
                    value: out var indexed
                ) ||
                    (indexed != pin) ||
                    !receipts.TryAdd(
                    key: receipt.OperationId,
                    value: receipt
                )
                ) {
                    throw new InvalidDataException(message: "receipt snapshot chain and operation index disagree");
                }
                pin = previous;
            }
            if (visited.Count != Nodes.Count) { throw new InvalidDataException(message: "receipt snapshot contains nodes outside its source chain"); }
            return receipts;
        } catch (JsonException error) {
            throw new InvalidDataException(
            innerException: error,
            message: "receipt snapshot index is malformed"
        );
        }
    }
}
