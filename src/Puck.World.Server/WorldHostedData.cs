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

    /// <summary>Discards a deployment group's reachable hosted data: every world authority under the owner's private
    /// hosted namespace and the group's release record. It reads the deployment's declared policy itself and refuses
    /// unless it is <see cref="WorldHostedDataPolicy.Scratch"/>; refuses when the owner's container holds another
    /// group's record or discard; records the discard with a marker before it changes anything; stops the group's
    /// writers; deletes each world's root before the objects the root names; deletes the group record; and clears the
    /// marker last. While the marker stands, <see cref="WorldReleaseGroupStore.LoadAsync"/> refuses the group, so no
    /// resume, rollback or activation can follow a half-discarded record; only another discard finishes it. Release
    /// content, fixtures, the controller lease and other keys in the namespace are kept. Every step is idempotent, so a
    /// retry finishes an interrupted discard.</summary>
    /// <param name="readPolicy">Reads the deployment's live hosted-data declaration from its provider, never from a
    /// local copy that may be stale; it runs first, under the caller's controller lease.</param>
    /// <param name="store">The blob store the hosted data lives in.</param>
    /// <param name="target">The storage target of the owner's private container.</param>
    /// <param name="owner">The deployment group's owner, whose private container holds exactly this group's worlds.</param>
    /// <param name="deploymentGroup">The deployment group whose hosted data is discarded.</param>
    /// <param name="stopWriters">Stops every process that writes this hosted data and returns once none runs, or throws
    /// when it finds a writer it may not stop. It runs before anything is deleted.</param>
    /// <param name="cancellationToken">The token that cancels the stop and the deletions; a cancelled discard is finished
    /// by the next one.</param>
    /// <returns>How many worlds and objects were deleted, and whether a group record existed.</returns>
    /// <exception cref="InvalidOperationException">The live policy is <see cref="WorldHostedDataPolicy.Durable"/>, or the
    /// owner's container holds another deployment group.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="readPolicy"/>, <paramref name="store"/>,
    /// <paramref name="target"/> or <paramref name="stopWriters"/> is <see langword="null"/>.</exception>
    public static async Task<WorldHostedDataDiscard> DiscardAsync(Func<CancellationToken, Task<WorldHostedDataPolicy>> readPolicy, IObjectBlobStore store,
        ObjectStorageTarget target, Guid owner, string deploymentGroup, Func<CancellationToken, Task> stopWriters, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: readPolicy);
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);
        ArgumentNullException.ThrowIfNull(argument: stopWriters);
        if (await readPolicy(arg: cancellationToken).ConfigureAwait(continueOnCapturedContext: false) != WorldHostedDataPolicy.Scratch) {
            throw new InvalidOperationException(message: "the deployment declares its hosted data durable; only scratch hosted data is discarded");
        }
        await RequireSoleGroupAsync(
            cancellationToken: cancellationToken,
            group: deploymentGroup,
            owner: owner,
            store: store,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false);
        var marker = WorldReleaseGroupStore.DiscardMarkerAddress(group: deploymentGroup, owner: owner);
        var written = await store.WriteAsync(
            target,
            marker,
            System.Text.Encoding.UTF8.GetBytes(s: deploymentGroup),
            ObjectBlobWriteMode.Overwrite,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!written.Succeeded) { throw new IOException(message: "the discard marker could not be written; nothing was discarded"); }
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
            WorldReleaseGroupStore.RecordAddress(group: deploymentGroup, owner: owner),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        _ = await store.DeleteAsync(target, marker, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return new(
            DeletedObjects: deleted,
            GroupRecordDeleted: hadRecord,
            Worlds: [.. worlds.Keys]
        );
    }

    /// <summary>Refuses when the owner's private container holds a release record or discard marker of any group but
    /// <paramref name="group"/>: an owner container holds exactly one deployment group, because world authority keys
    /// carry no group and a discard deletes every one of them.</summary>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The storage target of the owner's private container.</param>
    /// <param name="owner">The owner whose container is checked.</param>
    /// <param name="group">The one group the container may hold.</param>
    /// <param name="cancellationToken">The token that cancels the listing.</param>
    /// <returns>A task that completes when the container holds no other group.</returns>
    /// <exception cref="InvalidOperationException">Another group's record or discard marker exists.</exception>
    internal static async Task RequireSoleGroupAsync(IObjectBlobStore store, ObjectStorageTarget target, Guid owner, string group, CancellationToken cancellationToken) {
        var prefix = WorldReleaseGroupStore.GroupKeyPrefix;
        var others = (await store.ListAsync(
            target,
            owner,
            prefix,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false))
            .Where(predicate: static key => key.EndsWith(value: WorldReleaseGroupStore.RecordSuffix, comparisonType: StringComparison.Ordinal) ||
                key.EndsWith(value: WorldReleaseGroupStore.DiscardMarkerSuffix, comparisonType: StringComparison.Ordinal))
            .Select(selector: key => key[prefix.Length..key.LastIndexOf(value: '.')])
            .Where(predicate: name => !string.Equals(a: name, b: group, comparisonType: StringComparison.Ordinal))
            .Distinct(comparer: StringComparer.Ordinal)
            .Order(comparer: StringComparer.Ordinal)
            .ToArray();

        if (others.Length != 0) {
            throw new InvalidOperationException(message: $"the owner's container already holds deployment group {string.Join(separator: ", ", values: others)}; one owner container holds one group, since its world authorities carry no group");
        }
    }
}

/// <summary>What <see cref="WorldHostedData.DiscardAsync"/> deleted.</summary>
/// <param name="Worlds">The worlds whose authority objects were found and deleted, in ordinal order.</param>
/// <param name="DeletedObjects">The number of authority objects that existed and were deleted.</param>
/// <param name="GroupRecordDeleted">Whether the group's release record existed and was deleted.</param>
public sealed record WorldHostedDataDiscard(IReadOnlyList<string> Worlds, int DeletedObjects, bool GroupRecordDeleted);
