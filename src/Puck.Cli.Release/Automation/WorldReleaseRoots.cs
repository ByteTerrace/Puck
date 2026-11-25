using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>The provider-neutral storage steps a durable operation's release runtime takes over its source's drained
/// roots: preparing them for the candidate at Activate, and restoring the protected roots when pre-commit recovery runs.
/// Every hosting runtime and the local rehearsal call these, so the roots a deployment leaves are the roots a rehearsal
/// proves.</summary>
public static class WorldReleaseRoots {
    /// <summary>Checks that protected roots name exactly the given inventory, one root per world.</summary>
    /// <param name="roots">The protected roots, by root key.</param>
    /// <param name="identities">The source's world inventory.</param>
    /// <exception cref="InvalidDataException">The roots name another inventory.</exception>
    public static void Validate(IReadOnlyDictionary<string, string> roots, IReadOnlyCollection<WorldAuthorityIdentity> identities) {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(identities);
        if (
            (roots.Count != identities.Count) ||
            !roots.Keys.ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: identities.Select(selector: static identity => identity.RootKey))
        ) {
            throw new InvalidDataException(message: "protected source roots do not match the release inventory");
        }
    }
    /// <summary>Prepares each drained root for the operation's candidate. A reset operation
    /// (<see cref="WorldReleaseGroupRecord.PendingStateReset"/>) replaces every root with the target's definition, no
    /// gameplay and no rewind boundary; any other operation carries each world whose definition changes, or every world
    /// when it returns to the previous release, through its metadata transition.</summary>
    /// <param name="authority">The authority store holding the drained roots.</param>
    /// <param name="operation">The durable operation, in its Activate phase.</param>
    /// <param name="source">The release the roots were drained from.</param>
    /// <param name="target">The candidate release.</param>
    /// <param name="archive">The archive retaining both releases' files.</param>
    /// <param name="identities">The target's world inventory.</param>
    /// <param name="cancellationToken">The token that cancels storage reads and writes.</param>
    /// <returns>Why a world's root was refused, naming the world, or <see langword="null"/> when every root is
    /// prepared.</returns>
    public static async Task<string?> PrepareCandidateAsync(WorldAuthorityBlobStore authority, WorldReleaseGroupRecord operation, WorldReleaseManifest source,
        WorldReleaseManifest target, WorldReleaseArchive archive, IEnumerable<WorldAuthorityIdentity> identities, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(identities);

        foreach (var identity in identities) {
            if (operation.PendingStateReset is not null) {
                var reset = await authority.PrepareReleaseResetAsync(
                    identity,
                    operation,
                    target,
                    archive,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!reset.Ok) { return $"'{identity.World}' release reset refused: {reset.Detail}"; }
                continue;
            }
            if (!operation.ReturnsToPreviousRelease && (source.Definitions[identity.RootKey] == target.Definitions[identity.RootKey])) { continue; }
            var publication = await authority.PrepareReleaseMetadataAsync(
                identity,
                operation,
                source,
                target,
                archive,
                cancellationToken,
                CliWorldVocabulary.EnsureInstalled()
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!publication.Ok) { return $"'{identity.World}' release definition publication refused: {publication.Detail}"; }
        }
        return null;
    }
    /// <summary>Restores the operation's protected source roots under fresh maintenance fences, without starting a
    /// source. Every protected root is read before the first is restored, so a missing one restores nothing; the steps
    /// are safe to repeat while the operation stays in Recover.</summary>
    /// <param name="authority">The authority store holding the roots.</param>
    /// <param name="operation">The durable operation, in its Recover phase.</param>
    /// <param name="identities">The source's world inventory.</param>
    /// <param name="guard">Runs before each world's takeover and throws when the caller no longer holds the operation (a
    /// hosting runtime rereads the durable record under its lease).</param>
    /// <param name="cancellationToken">The token that cancels storage reads and writes.</param>
    /// <exception cref="InvalidDataException">The protected roots name another inventory, or one is missing.</exception>
    /// <exception cref="IOException">A world's maintenance ownership or restore was refused.</exception>
    public static async Task RestoreSourceAsync(WorldAuthorityBlobStore authority, WorldReleaseGroupRecord operation, IReadOnlyCollection<WorldAuthorityIdentity> identities,
        Func<CancellationToken, Task> guard, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(guard);
        Validate(identities: identities, roots: operation.RecoveryRoots);
        var operationId = operation.PendingOperationId!.Value;

        foreach (var identity in identities) {
            _ = (await authority.LoadRecoveryRootAsync(
                identity,
                operation.RecoveryRoots[identity.RootKey],
                operationId,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false)
                ?? throw new InvalidDataException(message: "a protected source root is missing"));
        }
        foreach (var identity in identities) {
            await guard(arg: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            var fence = (await authority.AcquireActivationAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false)
                ?? throw new IOException(message: "source recovery could not acquire maintenance ownership"));
            var restored = await authority.RestoreRecoveryRootAsync(
                identity,
                operation.RecoveryRoots[identity.RootKey],
                operationId,
                fence,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!restored.Ok) { throw new IOException(message: $"source recovery failed for '{identity.World}': {restored.Detail}"); }
        }
    }
}
