using Puck.Storage;

namespace Puck.World.Server;

/// <summary><see cref="IWorldAuthorityStore"/> over <see cref="IObjectBlobStore"/>, addressed through
/// <see cref="WorldOwnedWorldSync.HostedAddressFor"/> — fail-closed exactly like <see cref="WorldOwnedWorldSync"/>:
/// every operation is bounded by <see cref="OperationTimeout"/>, and both refusal axes an
/// <see cref="ObjectBlobWriteResult"/> can carry (a create-only loss, an if-match precondition loss) surface by name
/// rather than collapsing into one generic failure. A checkpoint blob is content-addressed and written create-only,
/// so a retry that resends identical bytes is idempotent; the <c>checkpoints/latest</c> pointer and a journal page
/// each move under their own if-match compare-and-swap, retried up to <see cref="MaxCasAttempts"/> times against a
/// concurrent writer before refusing by name.</summary>
public sealed class WorldAuthorityBlobStore : IWorldAuthorityStore {
    private const int MaxCasAttempts = 5;

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(seconds: 15);

    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;

    /// <summary>Initializes the store.</summary>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The storage target — the Azure account in deployment, a directory for local runs and the
    /// canary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public WorldAuthorityBlobStore(IObjectBlobStore store, ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);

        m_store = store;
        m_target = target;
    }

    // Every store call in this type runs under the SAME bound: a linked token that cancels after OperationTimeout
    // regardless of what the caller's own token does. Exception handling stays with each call site — some convert a
    // failure into WorldAuthorityStoreOutcome.Failed with their own wording, others let it propagate — so this only
    // wraps the timeout, never a try/catch.
    private static async Task<T> UnderTimeoutAsync<T>(CancellationToken cancellationToken, Func<CancellationToken, ValueTask<T>> op) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

        timeout.CancelAfter(delay: OperationTimeout);

        return await op(timeout.Token);
    }
    // sha256-64/{hex} is the canonical pin form (WorldDefinitionFileSource.ComputeContentHash); the checkpoint blob
    // NAME carries only the hex half, since '/' cannot live inside one path segment. This is the one place that
    // splits the pin, and CheckpointAddress is the one place that rejoins it.
    private static string ExtractHex(string hash) {
        const string Prefix = "sha256-64/";

        if (!hash.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Prefix
        )) {
            throw new InvalidDataException(message: $"'{hash}' is not a sha256-64 content-address pin.");
        }

        return hash[Prefix.Length..];
    }
    private static ObjectBlobAddress CheckpointAddress(Guid containerId, SafeName world, long ordinal, string hash) => WorldOwnedWorldSync.HostedAddressFor(
        containerId: containerId,
        leaf: $"checkpoints/{ordinal:D12}-{ExtractHex(hash: hash)}.pckp",
        world: world
    );
    private static ObjectBlobAddress JournalAddress(Guid containerId, SafeName world, long ordinal) => WorldOwnedWorldSync.HostedAddressFor(
        containerId: containerId,
        leaf: $"journal/{ordinal:D12}.bin",
        world: world
    );
    private static ObjectBlobAddress LatestPointerAddress(Guid containerId, SafeName world) => WorldOwnedWorldSync.HostedAddressFor(
        containerId: containerId,
        leaf: "checkpoints/latest",
        world: world
    );

    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> AppendJournalAsync(WorldAuthorityIdentity identity, WorldMutationJournalEntry entry, CancellationToken cancellationToken) {
        var pointerAddress = LatestPointerAddress(
            containerId: identity.Owner,
            world: identity.World
        );
        ObjectBlobContent? pointerContent;

        try {
            pointerContent = await UnderTimeoutAsync(
                cancellationToken: cancellationToken,
                op: ct => m_store.ReadAsync(
                    address: pointerAddress,
                    cancellationToken: ct,
                    target: m_target
                )
            );
        } catch (Exception exception) {
            return WorldAuthorityStoreOutcome.Failed(detail: $"reading '{pointerAddress.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        if (pointerContent is not { } pointer) {
            return WorldAuthorityStoreOutcome.Failed(detail: "no checkpoint exists yet — a journal is relative to one");
        }

        if (!WorldAuthorityStoreWireCodec.TryDecodeLatestPointer(
            bytes: pointer.Content.Span,
            hash: out _,
            ordinal: out var checkpointOrdinal,
            reason: out var pointerReason,
            tick: out _
        )) {
            return WorldAuthorityStoreOutcome.Failed(detail: $"'{pointerAddress.Key}' is corrupt — {pointerReason}");
        }

        var journalAddress = JournalAddress(
            containerId: identity.Owner,
            ordinal: checkpointOrdinal,
            world: identity.World
        );

        for (var attempt = 0; (attempt < MaxCasAttempts); attempt++) {
            ObjectBlobContent? current;

            try {
                current = await UnderTimeoutAsync(
                    cancellationToken: cancellationToken,
                    op: ct => m_store.ReadAsync(
                        address: journalAddress,
                        cancellationToken: ct,
                        target: m_target
                    )
                );
            } catch (Exception exception) {
                return WorldAuthorityStoreOutcome.Failed(detail: $"reading '{journalAddress.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }

            IReadOnlyList<WorldMutationJournalEntry> existing = [];

            if (current is { } found) {
                if (!WorldAuthorityStoreWireCodec.TryDecodeJournalPage(
                    bytes: found.Content.Span,
                    entries: out existing,
                    reason: out var pageReason
                )) {
                    return WorldAuthorityStoreOutcome.Failed(detail: $"'{journalAddress.Key}' is corrupt — {pageReason}");
                }
            }

            var appended = new List<WorldMutationJournalEntry>(capacity: (existing.Count + 1));

            appended.AddRange(collection: existing);
            appended.Add(item: entry);

            var pageBytes = WorldAuthorityStoreWireCodec.EncodeJournalPage(entries: appended);
            ObjectBlobWriteResult result;

            try {
                result = await UnderTimeoutAsync(
                    cancellationToken: cancellationToken,
                    op: ct => m_store.WriteAsync(
                        address: journalAddress,
                        cancellationToken: ct,
                        content: pageBytes,
                        ifMatchVersion: current?.VersionToken,
                        mode: ((current is null)
                            ? ObjectBlobWriteMode.CreateOnly
                            : ObjectBlobWriteMode.Overwrite
                        ),
                        target: m_target
                    )
                );
            } catch (Exception exception) {
                return WorldAuthorityStoreOutcome.Failed(detail: $"writing '{journalAddress.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }

            if (result.Succeeded) {
                return WorldAuthorityStoreOutcome.Success();
            }
            // A concurrent appender raced this attempt — re-read and retry rather than losing the entry.
        }

        return WorldAuthorityStoreOutcome.Failed(detail: $"'{journalAddress.Key}' append lost the compare-and-swap race {MaxCasAttempts} times in a row");
    }
    /// <inheritdoc/>
    public async Task<WorldDefinition?> LoadDefinitionAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var origin = new WorldHostedOrigin(
            owner: identity.Owner,
            store: m_store,
            target: m_target,
            world: identity.World
        );

        return await Task.Run(
            cancellationToken: cancellationToken,
            function: () => {
                if (origin.TryLoad(
                    definition: out var definition,
                    instanceIdentity: identity.World.Value,
                    reason: out var reason
                )) {
                    return definition;
                }

                if (reason.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "no cloud copy at"
                )) {
                    return null;
                }

                throw new InvalidDataException(message: $"hosted definition for '{origin.Identity}' failed to load: {reason}");
            }
        );
    }
    /// <inheritdoc/>
    public async Task<WorldMutationJournalTail> LoadJournalTailAsync(WorldAuthorityIdentity identity, long afterOrdinal, CancellationToken cancellationToken) {
        var address = JournalAddress(
            containerId: identity.Owner,
            ordinal: afterOrdinal,
            world: identity.World
        );

        var content = await UnderTimeoutAsync(
            cancellationToken: cancellationToken,
            op: ct => m_store.ReadAsync(
                address: address,
                cancellationToken: ct,
                target: m_target
            )
        );

        if (content is not { } found) {
            return new WorldMutationJournalTail(
                CheckpointOrdinal: afterOrdinal,
                Entries: []
            );
        }

        if (!WorldAuthorityStoreWireCodec.TryDecodeJournalPage(
            bytes: found.Content.Span,
            entries: out var entries,
            reason: out var reason
        )) {
            throw new InvalidDataException(message: $"'{address.Key}' is corrupt — {reason}");
        }

        return new WorldMutationJournalTail(
            CheckpointOrdinal: afterOrdinal,
            Entries: entries
        );
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityCheckpointBlob?> LoadLatestAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var pointerAddress = LatestPointerAddress(
            containerId: identity.Owner,
            world: identity.World
        );

        var pointerContent = await UnderTimeoutAsync(
            cancellationToken: cancellationToken,
            op: ct => m_store.ReadAsync(
                address: pointerAddress,
                cancellationToken: ct,
                target: m_target
            )
        );

        if (pointerContent is not { } pointer) {
            return null;
        }

        if (!WorldAuthorityStoreWireCodec.TryDecodeLatestPointer(
            bytes: pointer.Content.Span,
            hash: out var hash,
            ordinal: out var ordinal,
            reason: out var pointerReason,
            tick: out var tick
        )) {
            throw new InvalidDataException(message: $"'{pointerAddress.Key}' is corrupt — {pointerReason}");
        }

        var checkpointAddress = CheckpointAddress(
            containerId: identity.Owner,
            hash: hash,
            ordinal: ordinal,
            world: identity.World
        );

        var checkpointContent = await UnderTimeoutAsync(
            cancellationToken: cancellationToken,
            op: ct => m_store.ReadAsync(
                address: checkpointAddress,
                cancellationToken: ct,
                target: m_target
            )
        );

        if (checkpointContent is not { } checkpoint) {
            throw new InvalidDataException(message: $"'{pointerAddress.Key}' names '{checkpointAddress.Key}', which does not exist.");
        }

        var computedHash = WorldDefinitionFileSource.ComputeContentHash(content: checkpoint.Content.Span);

        if (!string.Equals(
            a: computedHash,
            b: hash,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new InvalidDataException(message: $"'{checkpointAddress.Key}' hashes to {computedHash}, not the pointer's recorded {hash}.");
        }

        return new WorldAuthorityCheckpointBlob(
            Encoded: checkpoint.Content,
            Ordinal: ordinal,
            Tick: tick
        );
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> PublishDefinitionAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: composed);

        var address = WorldOwnedWorldSync.HostedAddressFor(
            containerId: identity.Owner,
            leaf: "definition.json",
            world: identity.World
        );
        var bytes = WorldDefinitionSerialization.Serialize(definition: composed);

        try {
            var result = await UnderTimeoutAsync(
                cancellationToken: cancellationToken,
                op: ct => m_store.WriteAsync(
                    address: address,
                    cancellationToken: ct,
                    content: bytes,
                    mode: ObjectBlobWriteMode.Overwrite,
                    target: m_target
                )
            );

            return (result.Succeeded
                ? WorldAuthorityStoreOutcome.Success()
                : WorldAuthorityStoreOutcome.Failed(detail: $"writing '{address.Key}' did not succeed")
            );
        } catch (Exception exception) {
            return WorldAuthorityStoreOutcome.Failed(detail: $"writing '{address.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> WriteCheckpointAsync(WorldAuthorityIdentity identity, ReadOnlyMemory<byte> encoded, ulong tick, CancellationToken cancellationToken) {
        var hash = WorldDefinitionFileSource.ComputeContentHash(content: encoded.Span);
        var pointerAddress = LatestPointerAddress(
            containerId: identity.Owner,
            world: identity.World
        );

        for (var attempt = 0; (attempt < MaxCasAttempts); attempt++) {
            ObjectBlobContent? pointerContent;

            try {
                pointerContent = await UnderTimeoutAsync(
                    cancellationToken: cancellationToken,
                    op: ct => m_store.ReadAsync(
                        address: pointerAddress,
                        cancellationToken: ct,
                        target: m_target
                    )
                );
            } catch (Exception exception) {
                return WorldAuthorityStoreOutcome.Failed(detail: $"reading '{pointerAddress.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }

            var nextOrdinal = 0L;

            if (pointerContent is { } pointer) {
                if (!WorldAuthorityStoreWireCodec.TryDecodeLatestPointer(
                    bytes: pointer.Content.Span,
                    hash: out _,
                    ordinal: out var currentOrdinal,
                    reason: out var pointerReason,
                    tick: out _
                )) {
                    return WorldAuthorityStoreOutcome.Failed(detail: $"'{pointerAddress.Key}' is corrupt — {pointerReason}");
                }

                nextOrdinal = (currentOrdinal + 1);
            }

            var checkpointAddress = CheckpointAddress(
                containerId: identity.Owner,
                hash: hash,
                ordinal: nextOrdinal,
                world: identity.World
            );

            ObjectBlobWriteResult checkpointResult;

            try {
                checkpointResult = await UnderTimeoutAsync(
                    cancellationToken: cancellationToken,
                    op: ct => m_store.WriteAsync(
                        address: checkpointAddress,
                        cancellationToken: ct,
                        content: encoded,
                        mode: ObjectBlobWriteMode.CreateOnly,
                        target: m_target
                    )
                );
            } catch (Exception exception) {
                return WorldAuthorityStoreOutcome.Failed(detail: $"writing '{checkpointAddress.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }

            if (!checkpointResult.Succeeded) {
                // Content-addressed, so a create-only loss at this exact ordinal+hash is only ever a benign retry of
                // this same write landing twice — verified by reading back and comparing bytes, never assumed.
                ObjectBlobContent? existing;

                try {
                    existing = await UnderTimeoutAsync(
                        cancellationToken: cancellationToken,
                        op: ct => m_store.ReadAsync(
                            address: checkpointAddress,
                            cancellationToken: ct,
                            target: m_target
                        )
                    );
                } catch (Exception exception) {
                    return WorldAuthorityStoreOutcome.Failed(detail: $"'{checkpointAddress.Key}' exists and could not be read to compare — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
                }

                if (
                    (existing is not { } found) ||
                    !found.Content.Span.SequenceEqual(other: encoded.Span)
                ) {
                    return WorldAuthorityStoreOutcome.AlreadyExists(detail: $"'{checkpointAddress.Key}' exists with different content");
                }
            }

            var pointerBytes = WorldAuthorityStoreWireCodec.EncodeLatestPointer(
                hash: hash,
                ordinal: nextOrdinal,
                tick: tick
            );
            ObjectBlobWriteResult pointerResult;

            try {
                pointerResult = await UnderTimeoutAsync(
                    cancellationToken: cancellationToken,
                    op: ct => m_store.WriteAsync(
                        address: pointerAddress,
                        cancellationToken: ct,
                        content: pointerBytes,
                        ifMatchVersion: pointerContent?.VersionToken,
                        mode: ((pointerContent is null)
                            ? ObjectBlobWriteMode.CreateOnly
                            : ObjectBlobWriteMode.Overwrite
                        ),
                        target: m_target
                    )
                );
            } catch (Exception exception) {
                return WorldAuthorityStoreOutcome.Failed(detail: $"writing '{pointerAddress.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }

            if (pointerResult.Succeeded) {
                return WorldAuthorityStoreOutcome.Success();
            }
            // Another writer advanced the pointer between our read and our CAS — recompute the next ordinal and retry.
        }

        return WorldAuthorityStoreOutcome.Failed(detail: $"'{pointerAddress.Key}' lost the compare-and-swap race {MaxCasAttempts} times in a row");
    }
}
