using System.Globalization;
using System.Runtime.CompilerServices;
using Puck.Assets;
using Puck.Storage;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldAuthorityBlobStore {
    private readonly record struct JournalSegment(
        ObjectBlobAddress Address,
        ReadOnlyMemory<byte> Bytes,
        IReadOnlyList<WorldAuthorityJournalEntry> Entries,
        int Offset
    );

    private static ObjectBlobAddress ArchiveAddress(WorldAuthorityIdentity identity, Guid operationId, ObjectBlobAddress source) {
        var prefixLength = AuthorityAddress(identity, "").Key.Length;
        return AuthorityAddress(identity, $"recovery/{operationId:D}/payload/{source.Key[prefixLength..]}");
    }

    private async Task<ReadOnlyMemory<byte>> ReadPinnedPayloadAsync(WorldAuthorityIdentity identity, Guid archive,
        ObjectBlobAddress address, string hash, CancellationToken token, bool archiveOnly = false) {
        var found = archiveOnly ? null : await ReadAsync(address, token).ConfigureAwait(false);
        if (found is { } live && WorldDefinitionFileSource.ComputeContentHash(live.Content.Span) == hash) {
            return live.Content;
        }
        if (archive != Guid.Empty) {
            found = await ReadAsync(ArchiveAddress(identity, archive, address), token).ConfigureAwait(false);
        }
        if (found is not { } content || WorldDefinitionFileSource.ComputeContentHash(content.Content.Span) != hash) {
            throw new InvalidDataException($"Authority payload '{address.Key}' is missing or does not match its content pin.");
        }
        return content.Content;
    }

    // Follow at most the uncovered slice. A covering checkpoint may retire a segment even though the
    // oldest live segment still names it. A checkpoint may also cover a prefix within that oldest segment.
    private async IAsyncEnumerable<JournalSegment> ReadJournalSegmentsAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root,
        [EnumeratorCancellation] CancellationToken token, bool verifySegmentCount = true, Guid archiveOnly = default) {
        var remaining = root.JournalEntryCount;
        var sequence = root.JournalStorageSequence;
        var hash = root.JournalHash;
        var segmentCount = 0;

        while (remaining > 0) {
            if (hash is null || sequence <= 0) {
                throw new InvalidDataException("Journal ended before reaching checkpoint coverage.");
            }
            var address = JournalCandidateAddress(identity, sequence, hash);
            var bytes = await ReadPinnedPayloadAsync(identity, archiveOnly == Guid.Empty ? root.PayloadArchive : archiveOnly,
                address, hash, token, archiveOnly != Guid.Empty).ConfigureAwait(false);

            if (!WorldAuthorityStoreWireCodec.TryDecodeJournalSegment(bytes.Span, out var entries, out var previousSequence, out var previousHash, out var reason)) {
                throw new InvalidDataException($"Journal segment is corrupt: {reason}");
            }
            if (previousSequence >= sequence) {
                throw new InvalidDataException("Journal predecessor does not precede its successor.");
            }
            var take = Math.Min(remaining, entries.Count);
            remaining -= take;
            segmentCount++;
            yield return new(address, bytes, entries, entries.Count - take);
            sequence = previousSequence;
            hash = previousHash;
        }

        if (verifySegmentCount && segmentCount != root.JournalSegmentCount) {
            throw new InvalidDataException("Journal segment count disagrees with the root.");
        }
    }

    private async Task ArchiveRecoveryPayloadsAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, Guid operationId,
        CancellationToken token) {
        async Task ArchiveAsync(ObjectBlobAddress address, ReadOnlyMemory<byte> bytes) {
            var written = await PutImmutableAsync(ArchiveAddress(identity, operationId, address), bytes, token).ConfigureAwait(false);
            if (!written.Ok) { throw new IOException(written.Detail); }
        }
        if (root.CheckpointHash is { } hash) {
            var address = CheckpointCandidateAddress(identity, root.CheckpointStorageSequence, hash);
            var bytes = await ReadPinnedPayloadAsync(identity, root.PayloadArchive, address, hash, token).ConfigureAwait(false);
            await ArchiveAsync(address, bytes).ConfigureAwait(false);
        }
        await foreach (var segment in ReadJournalSegmentsAsync(identity, root, token).ConfigureAwait(false)) {
            await ArchiveAsync(segment.Address, segment.Bytes).ConfigureAwait(false);
        }
    }

    // Publication sequence never rewinds. A writer that can still win a CAS after this checkpoint must
    // use a newer sequence, and a restore can only revive payloads under its protected archive.
    private async Task<WorldAuthorityStoreOutcome> RetireCheckpointHistoryAsync(WorldAuthorityIdentity identity,
        WorldAuthorityRoot previous, HashSet<string> liveJournal, WorldAuthorityStoreOutcome published, CancellationToken token) {
        var retired = 0;
        var previousCheckpoint = previous.CheckpointHash is { } hash
            ? CheckpointCandidateAddress(identity, previous.CheckpointStorageSequence, hash).Key
            : null;

        async Task RetireAsync(string leaf, string fileExtension, Func<string, bool> isLive) {
            var address = AuthorityAddress(identity, leaf);
            var keys = await UnderTimeoutAsync(token, ct => m_store.ListAsync(m_target, address.ObjectId, address.Key, ct)).ConfigureAwait(false);
            var superseded = keys.Where(key => {
                if (!key.StartsWith(address.Key, StringComparison.Ordinal) || !key.EndsWith(fileExtension, StringComparison.Ordinal) || isLive(key)) {
                    return false;
                }
                var name = key.AsSpan(address.Key.Length);
                const int SequenceDigits = 20;
                return name.Length == SequenceDigits + 1 + AssetContentHash.HexLength + fileExtension.Length &&
                    name[SequenceDigits] == '-' &&
                    !name.Slice(SequenceDigits + 1, AssetContentHash.HexLength).ContainsAnyExcept("0123456789abcdef") &&
                    long.TryParse(name[..SequenceDigits], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) &&
                    sequence > 0 && sequence <= previous.Sequence;
            });
            await Parallel.ForEachAsync(superseded, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = token }, async (key, ct) => {
                if (await UnderTimeoutAsync(ct, bounded => m_store.DeleteAsync(m_target, new(address.ObjectId, key), bounded)).ConfigureAwait(false)) {
                    Interlocked.Increment(ref retired);
                }
            }).ConfigureAwait(false);
        }

        try {
            await RetireAsync("journal/", ".bin", liveJournal.Contains).ConfigureAwait(false);
            await RetireAsync("checkpoints/", ".pckp", key => key == previousCheckpoint).ConfigureAwait(false);
            return published with { RetiredObjects = retired };
        } catch (Exception error) {
            // Cleanup is post-commit. A failed delete cannot turn a durable checkpoint into an uncertain write.
            return published with { RetiredObjects = retired, RetentionFailure = error.Message };
        }
    }
}
