using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>A bounded, compare-and-swap operation journal over Puck's existing local/cloud blob abstraction.</summary>
/// <remarks>One blob belongs to one live authority lineage. Requests and their causal recovery evidence are written
/// together before dispatch. Keep this address outside rewindable world saves, and never share it with a fork.
/// Terminal entries are retained for deduplication; a full journal refuses admission rather than forgetting history.
/// The host decides retention/rotation and must not reuse an operation id after rotating its history.</remarks>
public sealed class WorldExternalOperationJournal {
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;
    private readonly ObjectBlobAddress m_address;
    private readonly int m_maximumEntries;
    private readonly int m_maximumBytes;
    private readonly int m_maximumConflicts;

    /// <summary>Creates a journal with explicit host storage and capacity policy.</summary>
    /// <param name="store">The existing routed blob store.</param>
    /// <param name="target">The host's private persistence target.</param>
    /// <param name="address">A stable address dedicated to one authority lineage.</param>
    /// <param name="maximumEntries">Maximum retained operations, including terminal history.</param>
    /// <param name="maximumBytes">Maximum encoded journal size in bytes.</param>
    /// <param name="maximumConflicts">Maximum CAS attempts per commit or transition.</param>
    /// <exception cref="ArgumentNullException">The store or target is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity is not positive.</exception>
    public WorldExternalOperationJournal(IObjectBlobStore store, ObjectStorageTarget target, ObjectBlobAddress address,
        int maximumEntries, int maximumBytes, int maximumConflicts) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConflicts);
        m_store = store;
        m_target = target;
        m_address = address;
        m_maximumEntries = maximumEntries;
        m_maximumBytes = maximumBytes;
        m_maximumConflicts = maximumConflicts;
    }

    /// <summary>Reads detached entries for recovery, reconciliation, or host read-back.</summary>
    /// <param name="cancellationToken">Cancels storage work.</param>
    /// <returns>The persisted operations in commit order.</returns>
    /// <exception cref="InvalidDataException">The journal is malformed, oversized, or lacks a CAS token.</exception>
    /// <exception cref="JsonException">The JSON does not satisfy the strict journal schema.</exception>
    public async ValueTask<IReadOnlyList<WorldExternalOperationEntry>> ReadAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).Entries;

    /// <summary>Commits the request and its causal recovery evidence together. Repeating an identical request
    /// returns its existing entry; reusing an id with different input refuses.</summary>
    /// <param name="operation">The host-bound, immutable request.</param>
    /// <param name="cause">Authority-private, versioned recovery evidence for the triggering transition.</param>
    /// <param name="cancellationToken">Cancels storage work; an uncertain write must be read back.</param>
    /// <returns>The existing or newly committed entry.</returns>
    /// <exception cref="InvalidOperationException">An id conflicts or a capacity is exhausted.</exception>
    /// <exception cref="IOException">Storage fails or the conflict budget is exhausted.</exception>
    public async ValueTask<WorldExternalOperationEntry> CommitAsync(WorldExternalOperation operation, string cause,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.BindingIdentity);
        ArgumentNullException.ThrowIfNull(operation.Payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(cause);
        for (var attempt = 0; attempt < m_maximumConflicts; attempt++) {
            var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var previous = snapshot.Entries.FirstOrDefault(entry => entry.Operation.Id == operation.Id);
            if (previous is not null) {
                if (previous.Operation != operation) {
                    throw new InvalidOperationException("An external operation id was reused with different input.");
                }
                return previous;
            }
            if (snapshot.Entries.Length >= m_maximumEntries) {
                throw new InvalidOperationException("The external operation journal is full.");
            }
            var entry = new WorldExternalOperationEntry(operation, cause);
            if (await WriteAsync(snapshot, [.. snapshot.Entries, entry], cancellationToken).ConfigureAwait(false)) {
                return entry;
            }
        }
        throw new IOException("External operation commit exceeded its conflict budget; read back before retrying.");
    }

    internal async ValueTask<bool> TryTransitionAsync(WorldExternalOperationEntry expected,
        WorldExternalOperationResult result, CancellationToken cancellationToken) {
        for (var attempt = 0; attempt < m_maximumConflicts; attempt++) {
            var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var index = Array.FindIndex(snapshot.Entries, entry => entry.Operation.Id == expected.Operation.Id);
            if (index < 0) { return false; }
            var current = snapshot.Entries[index];
            if (current != expected) {
                // A concurrent status poll must not discard a late execution result. In particular, an
                // accepted asynchronous response can carry the only durable continuation. It may replace
                // uncertainty about the original claim, but never a newer running or terminal observation.
                var definitive = result.Status is WorldExternalOperationStatus.Succeeded or WorldExternalOperationStatus.Failed;
                var acceptedClaim = expected.Status == WorldExternalOperationStatus.Dispatching &&
                    result.Status == WorldExternalOperationStatus.Running && current.Status == WorldExternalOperationStatus.Unknown;
                if ((!definitive && !acceptedClaim) || current.Status is WorldExternalOperationStatus.Pending or WorldExternalOperationStatus.Succeeded or
                    WorldExternalOperationStatus.Failed || current.Operation != expected.Operation || current.Cause != expected.Cause) {
                    return false;
                }
            }
            snapshot.Entries[index] = current with { Status = result.Status, Result = result.Result };
            if (await WriteAsync(snapshot, snapshot.Entries, cancellationToken).ConfigureAwait(false)) { return true; }
        }
        throw new IOException("External operation transition exceeded its conflict budget; reconcile before retrying.");
    }

    private async ValueTask<Snapshot> LoadAsync(CancellationToken cancellationToken) {
        var content = await m_store.ReadAsync(m_target, m_address, cancellationToken).ConfigureAwait(false);
        if (content is not { } blob) { return new Snapshot([], null, false); }
        if (blob.Content.Length > m_maximumBytes || string.IsNullOrEmpty(blob.VersionToken)) {
            throw new InvalidDataException("An operation journal exceeds its byte limit or lacks a CAS version token.");
        }
        var document = JsonSerializer.Deserialize(blob.Content.Span, WorldExternalOperationJsonContext.Default.WorldExternalOperationDocument)
            ?? throw new InvalidDataException("An operation journal cannot be null.");
        if (document.Version != 1 || document.Entries is null || document.Entries.Length > m_maximumEntries) {
            throw new InvalidDataException("Unsupported or oversized operation journal.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries) {
            if (entry?.Operation is not { } operation || string.IsNullOrWhiteSpace(operation.Id) ||
                string.IsNullOrWhiteSpace(operation.Binding) || string.IsNullOrWhiteSpace(operation.BindingIdentity) || operation.Payload is null ||
                string.IsNullOrWhiteSpace(entry.Cause) || entry.Result is null || !Enum.IsDefined(entry.Status) || !ids.Add(operation.Id)) {
                throw new InvalidDataException("Malformed or duplicate operation journal entry.");
            }
        }
        return new Snapshot(document.Entries, blob.VersionToken, true);
    }

    private async ValueTask<bool> WriteAsync(Snapshot snapshot, WorldExternalOperationEntry[] entries,
        CancellationToken cancellationToken) {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new WorldExternalOperationDocument(1, entries),
            WorldExternalOperationJsonContext.Default.WorldExternalOperationDocument);
        if (bytes.Length > m_maximumBytes) { throw new InvalidOperationException("The external operation journal byte budget is exhausted."); }
        var result = await m_store.WriteAsync(m_target, m_address, bytes,
            snapshot.Exists ? ObjectBlobWriteMode.Overwrite : ObjectBlobWriteMode.CreateOnly,
            snapshot.Version, cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    private sealed record Snapshot(WorldExternalOperationEntry[] Entries, string? Version, bool Exists);
}

internal sealed record WorldExternalOperationDocument(int Version, WorldExternalOperationEntry[] Entries);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WorldExternalOperationDocument))]
internal sealed partial class WorldExternalOperationJsonContext : JsonSerializerContext;
