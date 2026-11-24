using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Whether a deployment group's hosted data must outlive a deployment. Hosted data is every world authority
/// under the group owner's private hosted namespace (each world's root, published definitions, checkpoints, journal,
/// receipts and recovery roots) and the group's release record.</summary>
public enum WorldHostedDataPolicy {
    /// <summary>Hosted data is kept: a deployment carries, resets or refuses it through the release transaction.</summary>
    Durable,
    /// <summary>Hosted data is disposable: a deployment discards it and starts its release as a first deployment.</summary>
    Scratch,
}

/// <summary>The policy a deployment declares for its hosted data, and the one door that discards that data.</summary>
public static class WorldHostedData {
    /// <summary>The configuration value that declares <see cref="WorldHostedDataPolicy.Scratch"/>.</summary>
    public const string ScratchValue = "scratch";
    /// <summary>The configuration value that declares <see cref="WorldHostedDataPolicy.Durable"/>.</summary>
    public const string DurableValue = "durable";

    /// <summary>Reads a deployment's declared policy. Only an explicit <see cref="ScratchValue"/> declares scratch: an
    /// absent declaration is durable, so data is never discarded by default.</summary>
    /// <param name="value">The declared value, or <see langword="null"/> when the deployment declares none.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="InvalidDataException"><paramref name="value"/> is neither <see cref="ScratchValue"/> nor
    /// <see cref="DurableValue"/>.</exception>
    public static WorldHostedDataPolicy ParsePolicy(string? value) => value switch {
        null or DurableValue => WorldHostedDataPolicy.Durable,
        ScratchValue => WorldHostedDataPolicy.Scratch,
        _ => throw new InvalidDataException(message: $"hosted data policy '{value}' is neither '{ScratchValue}' nor '{DurableValue}'"),
    };

    /// <summary>Discards a deployment group's hosted data: stops its writers, then deletes every world authority under
    /// the owner's private hosted namespace, each world's root before the objects it names, and the group's release
    /// record last. Release content, fixtures, the controller lease and other keys in the namespace are kept. Every step
    /// is idempotent, so a retry after a failure finishes the discard.</summary>
    /// <param name="policy">The deployment's declared policy; only <see cref="WorldHostedDataPolicy.Scratch"/> discards.</param>
    /// <param name="store">The blob store the hosted data lives in.</param>
    /// <param name="target">The storage target of the owner's private container.</param>
    /// <param name="owner">The deployment group's owner, whose private container holds exactly this group's worlds.</param>
    /// <param name="deploymentGroup">The deployment group whose release record is discarded.</param>
    /// <param name="stopWriters">Stops every process that writes this hosted data and returns once none runs. It runs
    /// before anything is deleted, so no live host writes over the discard.</param>
    /// <param name="cancellationToken">The token that cancels the stop and the deletions; a cancelled discard is retried
    /// by the next one.</param>
    /// <returns>How many worlds and objects were deleted, and whether a group record existed.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="policy"/> is
    /// <see cref="WorldHostedDataPolicy.Durable"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="store"/>, <paramref name="target"/> or
    /// <paramref name="stopWriters"/> is <see langword="null"/>.</exception>
    public static async Task<WorldHostedDataDiscard> DiscardAsync(WorldHostedDataPolicy policy, IObjectBlobStore store, ObjectStorageTarget target,
        Guid owner, string deploymentGroup, Func<CancellationToken, Task> stopWriters, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);
        ArgumentNullException.ThrowIfNull(argument: stopWriters);
        if (policy != WorldHostedDataPolicy.Scratch) {
            throw new InvalidOperationException(message: "the deployment declares its hosted data durable; only scratch hosted data is discarded");
        }
        var record = WorldReleaseGroupStore.RecordAddress(
            group: deploymentGroup,
            owner: owner
        );

        await stopWriters(arg: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var prefix = (WorldOwnedWorldSync.HostedPrivateNamespace + "/");
        var keys = await store.ListAsync(
            target,
            owner,
            prefix,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        // {prefix}{world}/authority/{leaf}: the world segment holds no '/', and only this segment shape is a world's.
        var worlds = new SortedDictionary<string, List<string>>(comparer: StringComparer.Ordinal);

        foreach (var key in keys) {
            if (!key.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)) { continue; }
            var rest = key.AsSpan(start: prefix.Length);
            var slash = rest.IndexOf(value: '/');

            if (
                (slash <= 0) ||
                !rest[(slash + 1)..].StartsWith(value: (WorldAuthorityBlobStore.AuthoritySegment + "/"), comparisonType: StringComparison.Ordinal)
            ) { continue; }
            var world = rest[..slash].ToString();

            if (!worlds.TryGetValue(key: world, value: out var objects)) {
                objects = [];
                worlds.Add(key: world, value: objects);
            }
            objects.Add(item: key);
        }
        var deleted = 0;

        foreach (var (world, objects) in worlds) {
            var root = $"{prefix}{world}/{WorldAuthorityBlobStore.AuthoritySegment}/{WorldAuthorityBlobStore.RootLeaf}";

            // The root names every other object, so it goes first: an interrupted discard leaves unreferenced objects,
            // never a root that points at deleted ones.
            if (await store.DeleteAsync(target, new(ObjectId: owner, Key: root), cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) { deleted++; }
            foreach (var key in objects) {
                if (string.Equals(a: key, b: root, comparisonType: StringComparison.Ordinal)) { continue; }
                if (await store.DeleteAsync(target, new(ObjectId: owner, Key: key), cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) { deleted++; }
            }
        }
        var hadRecord = await store.DeleteAsync(
            target,
            record,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return new(
            DeletedObjects: deleted,
            GroupRecordDeleted: hadRecord,
            Worlds: [.. worlds.Keys]
        );
    }
}

/// <summary>What <see cref="WorldHostedData.DiscardAsync"/> deleted.</summary>
/// <param name="Worlds">The worlds whose authority objects were found and deleted, in ordinal order.</param>
/// <param name="DeletedObjects">The number of authority objects that existed and were deleted.</param>
/// <param name="GroupRecordDeleted">Whether the group's release record existed and was deleted.</param>
public sealed record WorldHostedDataDiscard(IReadOnlyList<string> Worlds, int DeletedObjects, bool GroupRecordDeleted);
