namespace Puck.World.Server;

/// <summary>One checkpoint blob's raw encoded bytes plus the pointer facts that named it — the hash-verified answer
/// <see cref="IWorldAuthorityStore.LoadLatestAsync"/> returns. The bytes are opaque to the store: a checkpoint codec
/// decodes them into a simulation-state record elsewhere, so the store and that record format can land
/// independently.</summary>
/// <param name="Encoded">The checkpoint blob's raw bytes, hash-verified against the pointer that named them.</param>
/// <param name="Ordinal">The checkpoint's own ordinal.</param>
/// <param name="Tick">The engine tick the checkpoint was captured at.</param>
public readonly record struct WorldAuthorityCheckpointBlob(ReadOnlyMemory<byte> Encoded, long Ordinal, ulong Tick);
/// <summary>One mutation journal entry — opaque encoded bytes (a mutation-codec leaf, opaque to the store) plus the
/// engine tick it was recorded at.</summary>
/// <param name="Tick">The engine tick the mutation was recorded at.</param>
/// <param name="Encoded">The mutation's own encoded bytes.</param>
public readonly record struct WorldMutationJournalEntry(ulong Tick, ReadOnlyMemory<byte> Encoded);
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

    /// <summary>The write did not land for any other reason (transport, timeout, or a refused compare-and-swap
    /// retry ceiling).</summary>
    Failed,
}
/// <summary>The outcome of one <see cref="IWorldAuthorityStore"/> write.</summary>
/// <param name="Kind">What kind of thing happened.</param>
/// <param name="Detail">Human-readable detail — the remedy on a refusal, empty on success.</param>
public readonly record struct WorldAuthorityStoreOutcome(WorldAuthorityStoreOutcomeKind Kind, string Detail) {
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
    public static WorldAuthorityStoreOutcome Success(string detail = "") => new(
        Detail: detail,
        Kind: WorldAuthorityStoreOutcomeKind.Ok
    );
}
/// <summary>The blob-backed persistence seam a hosted world's checkpoint, journal, and published definition read and
/// write through. Programmed against opaque encoded bytes throughout — this seam never decodes a checkpoint or a
/// mutation leaf, so it lands independently of the record formats those bytes carry.</summary>
public interface IWorldAuthorityStore {
    /// <summary>Loads a hosted world's published, composed definition.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The definition, or <see langword="null"/> when none has been published.</returns>
    Task<WorldDefinition?> LoadDefinitionAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken);
    /// <summary>Loads a hosted world's latest checkpoint — the <c>checkpoints/latest</c> pointer, then the blob it
    /// names, hash-verified against the pointer.</summary>
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
    Task<WorldMutationJournalTail> LoadJournalTailAsync(WorldAuthorityIdentity identity, long afterOrdinal, CancellationToken cancellationToken);
    /// <summary>Writes a new checkpoint: the content-addressed blob (create-only, so an identical retry is
    /// idempotent), then the <c>checkpoints/latest</c> pointer (create-only when none exists, else if-match CAS
    /// against the pointer this call itself reads).</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="encoded">The checkpoint's raw encoded bytes.</param>
    /// <param name="tick">The engine tick the checkpoint was captured at.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The write outcome.</returns>
    Task<WorldAuthorityStoreOutcome> WriteCheckpointAsync(WorldAuthorityIdentity identity, ReadOnlyMemory<byte> encoded, ulong tick, CancellationToken cancellationToken);
    /// <summary>Appends one mutation to the journal tail of the CURRENT latest checkpoint (learned from
    /// <c>checkpoints/latest</c>) — a read-modify-write CAS loop against the journal's own if-match token, so two
    /// concurrent appends never silently clobber one another.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="entry">The mutation to append.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The write outcome; <see cref="WorldAuthorityStoreOutcomeKind.Failed"/> when no checkpoint has ever
    /// been written for this identity (a journal is always relative to one).</returns>
    Task<WorldAuthorityStoreOutcome> AppendJournalAsync(WorldAuthorityIdentity identity, WorldMutationJournalEntry entry, CancellationToken cancellationToken);
    /// <summary>Publishes a hosted world's composed definition — the one writer of <c>definition.json</c>.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="composed">The composed definition to publish.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The write outcome.</returns>
    Task<WorldAuthorityStoreOutcome> PublishDefinitionAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken cancellationToken);
}
