using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One checkpoint blob's raw encoded bytes plus the pointer facts that named it — the hash-verified answer
/// <see cref="IWorldAuthorityStore.LoadLatestAsync"/> returns. The bytes are opaque to the store: a checkpoint codec
/// decodes them into a simulation-state record elsewhere, so the store and that record format can land
/// independently.</summary>
/// <param name="Encoded">The checkpoint blob's raw bytes, hash-verified against the pointer that named them.</param>
/// <param name="Ordinal">The checkpoint's own ordinal.</param>
/// <param name="Tick">The engine tick the checkpoint was captured at.</param>
public readonly record struct WorldAuthorityCheckpointBlob(ReadOnlyMemory<byte> Encoded, long Ordinal, ulong Tick);
/// <summary>The mutation journal tail for one checkpoint ordinal — every entry recorded since that checkpoint, in
/// append order.</summary>
/// <param name="CheckpointOrdinal">The checkpoint ordinal this tail is relative to.</param>
/// <param name="Entries">The recorded entries, in append order; empty when nothing has been appended yet.</param>
public readonly record struct WorldMutationJournalTail(long CheckpointOrdinal, IReadOnlyList<WorldMutationJournalEntry> Entries);
/// <summary>What kind of thing happened to one store write — the fail-closed vocabulary every
/// <see cref="IWorldAuthorityStore"/> write answers with, naming both refusal axes an
/// <see cref="Puck.Storage.ObjectBlobWriteResult"/> can carry apart from a genuine transport failure.</summary>
public enum WorldAuthorityStoreOutcomeKind {
    /// <summary>The write landed.</summary>
    Ok,

    /// <summary>A create-only write lost because the blob already existed.</summary>
    AlreadyExists,

    /// <summary>An if-match write lost its precondition — the blob moved since the caller last read it.</summary>
    PreconditionFailed,

    /// <summary>The writer's activation fence is no longer current.</summary>
    StaleFence,

    /// <summary>The transport outcome is unknown and must be reconciled from the root.</summary>
    RecoveryRequired,

    /// <summary>The operation id was already recorded for a different actor or payload.</summary>
    OperationConflict,

    /// <summary>The write did not land for any other reason (transport, timeout, or a refused compare-and-swap
    /// retry ceiling).</summary>
    Failed,
}
/// <summary>The outcome of one <see cref="IWorldAuthorityStore"/> write.</summary>
/// <param name="Kind">What kind of thing happened.</param>
/// <param name="Detail">Human-readable detail — the remedy on a refusal, empty on success.</param>
public readonly record struct WorldAuthorityStoreOutcome(WorldAuthorityStoreOutcomeKind Kind, string Detail) {
    /// <summary>The root snapshot observed after a successful publication, including its journal watermark.</summary>
    public WorldAuthorityRootSnapshot? PublishedRoot { get; init; }
    /// <summary>Gets a value indicating whether the write landed.</summary>
    public bool Ok => (Kind == WorldAuthorityStoreOutcomeKind.Ok);

    /// <summary>Builds an already-exists outcome.</summary>
    /// <param name="detail">The refusal detail.</param>
    public static WorldAuthorityStoreOutcome AlreadyExists(string detail) => new(
        Detail: detail,
        Kind: WorldAuthorityStoreOutcomeKind.AlreadyExists
    );
    /// <summary>Builds a failed outcome.</summary>
    /// <param name="detail">The refusal detail.</param>
    public static WorldAuthorityStoreOutcome Failed(string detail) => new(
        Detail: detail,
        Kind: WorldAuthorityStoreOutcomeKind.Failed
    );
    /// <summary>Builds a precondition-failed outcome.</summary>
    /// <param name="detail">The refusal detail.</param>
    public static WorldAuthorityStoreOutcome PreconditionFailed(string detail) => new(
        Detail: detail,
        Kind: WorldAuthorityStoreOutcomeKind.PreconditionFailed
    );
    /// <summary>Builds a success outcome.</summary>
    /// <param name="detail">Optional human-readable detail.</param>
    /// <param name="root">The committed root snapshot, when this success came from a root publication.</param>
    public static WorldAuthorityStoreOutcome Success(string detail = "", WorldAuthorityRootSnapshot? root = null) => new(
        Detail: detail,
        Kind: WorldAuthorityStoreOutcomeKind.Ok
    ) { PublishedRoot = root };
    /// <summary>Builds a stale-fence outcome.</summary>
    public static WorldAuthorityStoreOutcome StaleFence(string detail) => new(
        Detail: detail,
        Kind: WorldAuthorityStoreOutcomeKind.StaleFence
    );
    /// <summary>Builds an operation-conflict outcome.</summary>
    public static WorldAuthorityStoreOutcome OperationConflict(string detail) => new(
        Detail: detail,
        Kind: WorldAuthorityStoreOutcomeKind.OperationConflict
    );
    /// <summary>Builds an uncertain-commit outcome.</summary>
    public static WorldAuthorityStoreOutcome RecoveryRequired(string detail) => new(
        Detail: detail,
        Kind: WorldAuthorityStoreOutcomeKind.RecoveryRequired
    );
}
/// <summary>The blob-backed persistence seam a hosted world's checkpoint, journal, and published definition read and
/// write through. Programmed against opaque encoded bytes throughout — this seam never decodes a checkpoint or a
/// mutation leaf, so it lands independently of the record formats those bytes carry.</summary>
public interface IWorldAuthorityStore {
    /// <summary>Reads the authoritative root. Once present, legacy mutable pointers are never read as authority.</summary>
    Task<WorldAuthorityRootSnapshot?> LoadRootAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken);
    /// <summary>Reads a coherent checkpoint and journal view named by one root publication.</summary>
    Task<WorldAuthorityRecovery?> LoadRecoveryAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken);
    /// <summary>Advances the activation epoch using one root CAS and returns the lease bound to that epoch.</summary>
    Task<WorldAuthorityFence?> AcquireActivationAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken);
    /// <summary>Publishes an unowned root with a new epoch, preventing the released lease from being reused.</summary>
    Task<WorldAuthorityStoreOutcome> ReleaseActivationAsync(WorldAuthorityIdentity identity, WorldAuthorityFence fence, CancellationToken cancellationToken);
    /// <summary>Finds an operation receipt by walking the immutable receipt chain named by the root.</summary>
    Task<WorldAuthorityOperationReceipt?> FindOperationReceiptAsync(WorldAuthorityIdentity identity, Guid operationId, CancellationToken cancellationToken);
    /// <summary>Loads a hosted world's published, composed definition.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The definition, or <see langword="null"/> when none has been published.</returns>
    Task<WorldDefinition?> LoadDefinitionAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken);
    /// <summary>Loads a hosted world's latest checkpoint named by the authoritative private root, then the immutable
    /// blob it references, hash-verified against that root (with the legacy pointer used only before root
    /// initialization).</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The latest checkpoint's raw bytes and pointer facts, or <see langword="null"/> when none has been
    /// captured yet.</returns>
    /// <exception cref="InvalidDataException">A checkpoint blob's content does not hash to what the pointer
    /// recorded.</exception>
    Task<WorldAuthorityCheckpointBlob?> LoadLatestAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken);
    /// <summary>Loads every mutation recorded since a checkpoint ordinal.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="afterOrdinal">The checkpoint ordinal to load the tail of.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The tail — empty when nothing has been appended since that checkpoint.</returns>
    /// <exception cref="InvalidDataException">A root is present but its checkpoint ordinal does not match
    /// <paramref name="afterOrdinal"/>.</exception>
    Task<WorldMutationJournalTail> LoadJournalTailAsync(WorldAuthorityIdentity identity, long afterOrdinal, CancellationToken cancellationToken);
    /// <summary>Writes a new checkpoint candidate and publishes it through the private authority root CAS.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="encoded">The checkpoint's raw encoded bytes.</param>
    /// <param name="tick">The engine tick the checkpoint was captured at.</param>
    /// <param name="capturedJournalSequence">The absolute journal sequence covered by the encoded snapshot; when
    /// omitted, the call refuses if any journal tail exists.</param>
    /// <param name="fence">The activation fence acquired by the owning writer. <see langword="null"/> is permitted
    /// only for epoch-zero bootstrap or an already-released empty-token root; it never acquires an active lease.</param>
    /// <param name="receipt">An optional operation receipt published atomically with the checkpoint.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The write outcome.</returns>
    Task<WorldAuthorityStoreOutcome> WriteCheckpointAsync(WorldAuthorityIdentity identity, ReadOnlyMemory<byte> encoded, ulong tick, CancellationToken cancellationToken, WorldAuthorityFence? fence = null, WorldAuthorityOperationReceipt? receipt = null, long? capturedJournalSequence = null);
    /// <summary>Appends one mutation to the journal tail of the CURRENT latest checkpoint named by the private root —
    /// a read-modify-write root-CAS loop, so two concurrent appends never silently clobber one another.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="entry">The mutation to append.</param>
    /// <param name="fence">The activation fence acquired by the owning writer. <see langword="null"/> is permitted
    /// only for epoch-zero bootstrap or an already-released empty-token root; it never acquires an active lease.</param>
    /// <param name="receipt">An optional operation receipt published atomically with the append.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The write outcome; <see cref="WorldAuthorityStoreOutcomeKind.Failed"/> when no checkpoint has ever
    /// been written for this identity (a journal is always relative to one).</returns>
    Task<WorldAuthorityStoreOutcome> AppendJournalAsync(WorldAuthorityIdentity identity, WorldMutationJournalEntry entry, CancellationToken cancellationToken, WorldAuthorityFence? fence = null, WorldAuthorityOperationReceipt? receipt = null);
    /// <summary>Publishes a hosted world's composed definition — the one writer of <c>definition.json</c>.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="composed">The composed definition to publish.</param>
    /// <param name="fence">The activation fence acquired by the owning writer. <see langword="null"/> is permitted
    /// only for epoch-zero bootstrap or an already-released empty-token root; it never acquires an active lease.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The write outcome.</returns>
    Task<WorldAuthorityStoreOutcome> PublishDefinitionAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken cancellationToken, WorldAuthorityFence? fence = null);
    /// <summary>Publishes a refused operation receipt without changing journal payload bytes. Applied receipts must
    /// accompany the journal or checkpoint CAS that made the mutation durable.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="receipt">The actor/payload-bound operation receipt.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <param name="fence">The activation fence acquired by the owning writer, or <see langword="null"/> for
    /// compatibility callers that acquire one for this operation.</param>
    Task<WorldAuthorityStoreOutcome> RecordReceiptAsync(WorldAuthorityIdentity identity, WorldAuthorityOperationReceipt receipt, CancellationToken cancellationToken, WorldAuthorityFence? fence = null);
}
