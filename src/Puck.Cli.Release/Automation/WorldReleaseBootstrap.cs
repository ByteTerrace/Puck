using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>The provider-neutral steps of a first deployment's bootstrap that every release runtime shares: publishing
/// the release's definitions through unowned roots, and capturing those roots as the operation's recovery roots.</summary>
public static class WorldReleaseBootstrap {
    /// <summary>Publishes each definition of a first release, as the exact bytes the archive retains, through an unowned
    /// authority root, after proving it admits beside the asset layout it runs in. A retry compares exact published bytes.
    /// It refuses any existing gameplay state, and a different published definition unless an earlier bootstrap of the
    /// same deployment group published it and recovered without admitting anyone.</summary>
    /// <param name="manifest">The release whose definitions are published.</param>
    /// <param name="archive">The archive retaining the release's files.</param>
    /// <param name="authority">The authority store the definitions are published into.</param>
    /// <param name="owner">The deployment's owner; every definition must belong to it.</param>
    /// <param name="documentDirectory">The local copy of the asset layout the deployed silo resolves the documents'
    /// relative paths beside, or <see langword="null"/> for documents that author none.</param>
    /// <param name="history">The deployment group's durable history. A bootstrap entry (one with no source release)
    /// names the recovery roots it captured, and a published definition equal to one of them is the group's own.</param>
    /// <param name="cancellationToken">The token that cancels storage reads and writes.</param>
    /// <exception cref="InvalidDataException">A definition belongs to another owner, does not admit, or would replace
    /// existing state or a different published definition that no earlier bootstrap of the group published.</exception>
    /// <exception cref="IOException">The authority store refuses a publication.</exception>
    public static async Task PublishAsync(WorldReleaseManifest manifest, WorldReleaseArchive archive,
        WorldAuthorityBlobStore authority, Guid owner, string? documentDirectory, IReadOnlyList<WorldReleaseGroupHistoryEntry> history,
        CancellationToken cancellationToken) {
        _ = CliWorldVocabulary.EnsureInstalled();
        var definitions = new List<(WorldAuthorityIdentity Identity, ReadOnlyMemory<byte> Bytes)>();

        foreach (var row in manifest.DefinitionFiles) {
            var parts = row.Key.Split('/');

            if (
                (parts.Length != 2) ||
                (parts[0] != owner.ToString(format: "D"))
            ) { throw new InvalidDataException(message: "bootstrap inventory does not belong to the configured owner"); }
            var identity = new WorldAuthorityIdentity(
                Owner: owner,
                World: SafeName.Parse(candidate: parts[1])
            );
            var bytes = await archive.ReadFileAsync(
                manifest,
                row.Value,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!WorldDefinitionLoader.TryReadPublishable(
                definition: out _,
                documentDirectory: documentDirectory,
                json: System.Text.Encoding.UTF8.GetString(bytes: bytes.Span),
                reason: out var reason,
                sourceName: row.Value
            )) { throw new InvalidDataException(message: reason); }
            var root = await authority.LoadRootAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (root is { Root.HoldsGameplay: true }) {
                throw new InvalidDataException(message: "bootstrap cannot replace existing authoritative gameplay state");
            }
            var current = await authority.LoadPublishedDefinitionBytesAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (current is { } publishedBytes) &&
                !publishedBytes.Span.SequenceEqual(other: bytes.Span) &&
                !await PublishedByRecoveredBootstrapAsync(
                    authority: authority,
                    cancellationToken: cancellationToken,
                    definitionHash: root?.Root.DefinitionHash,
                    history: history,
                    identity: identity
                ).ConfigureAwait(continueOnCapturedContext: false)
            ) {
                throw new InvalidDataException(message: $"bootstrap cannot replace '{identity.World}''s existing different world definition, which no earlier bootstrap of this group published");
            }
            definitions.Add(item: (identity, bytes));
        }
        foreach (var row in definitions) {
            var published = await authority.PublishDefinitionBytesAsync(
                row.Identity,
                row.Bytes,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!published.Ok) { throw new IOException(message: $"bootstrap definition publication refused: {published.Detail}"); }
        }
    }
    // A bootstrap publishes its definitions before it captures their recovery roots, so a recovered bootstrap's root for
    // this identity names the definition it published. Recovery admitted no one, so that definition holds nothing to keep.
    private static async Task<bool> PublishedByRecoveredBootstrapAsync(WorldAuthorityBlobStore authority, WorldAuthorityIdentity identity,
        string? definitionHash, IReadOnlyList<WorldReleaseGroupHistoryEntry> history, CancellationToken cancellationToken) {
        if (definitionHash is null) { return false; }
        foreach (var entry in history) {
            if (
                (entry.SourceRelease is not null) ||
                !entry.RecoveryRoots.TryGetValue(
                    key: identity.RootKey,
                    value: out var pin
                )
            ) { continue; }
            var saved = await authority.LoadRecoveryRootAsync(
                identity,
                pin,
                entry.OperationId,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (string.Equals(
                a: saved?.Root.DefinitionHash,
                b: definitionHash,
                comparisonType: StringComparison.Ordinal
            )) { return true; }
        }
        return false;
    }
    /// <summary>Captures each bootstrapped world's unowned root as the operation's recovery root, which pre-commit recovery
    /// restores.</summary>
    /// <param name="authority">The authority store the bootstrap published into.</param>
    /// <param name="identities">The release's world inventory.</param>
    /// <param name="operationId">The bootstrap operation the roots are captured under.</param>
    /// <param name="cancellationToken">The token that cancels storage reads and writes.</param>
    /// <returns>The captured roots' pins, by root key.</returns>
    /// <exception cref="InvalidDataException">A world has no root to capture, or its root is owned by an activation.</exception>
    public static async Task<SortedDictionary<string, string>> CaptureRootsAsync(WorldAuthorityBlobStore authority, IEnumerable<WorldAuthorityIdentity> identities,
        Guid operationId, CancellationToken cancellationToken) {
        var captured = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var identity in identities) {
            var saved = (await authority.CaptureRecoveryRootAsync(
                identity,
                operationId,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false)
                ?? throw new InvalidDataException(message: "bootstrap recovery root is missing"));

            if (saved.Root.FenceToken != Guid.Empty) { throw new InvalidDataException(message: "bootstrap cannot capture an owned world"); }
            captured[identity.RootKey] = saved.Pin;
        }
        return captured;
    }
}
