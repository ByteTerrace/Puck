using Puck.Storage;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>Applies the real release publisher to a disposable qualification copy. Both packaged engines
/// subsequently import this same copy; reversal uses the candidate's drained continuation, never the seed.</summary>
internal sealed class WorldReleaseQualificationTransition(IObjectBlobStore blobs, WorldReleaseArchive archive) {
    private static WorldReleaseGroupSnapshot Require(WorldReleaseGroupOutcome outcome) => ((outcome.Ok && (outcome.Snapshot is { } snapshot))
        ? snapshot
        : throw new InvalidDataException(message: ("metadata qualification preparation failed: " + outcome.Detail))
    );

    public async Task ApplyAsync(string directory, WorldSiloDefinition definition, WorldReleaseManifest source,
        WorldReleaseManifest target, CancellationToken token) {
        if (
            !WorldReleaseCompatibility.TryCheckStructuralCompatibility(
            candidate: target,
            previous: source,
            reason: out var reason
        ) ||
            !WorldReleaseCompatibility.TryRequireMetadataCoordinator(
            reason: out reason,
            source: source,
            target: target
        )
        ) {
            throw new InvalidDataException(message: reason);
        }
        var owner = definition.Worlds.Select(selector: row => row.Owner).Distinct().Single();

        if (!definition.Worlds.Select(selector: row => $"{row.Owner:D}/{row.World}").ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: source.Definitions.Keys)) {
            throw new InvalidDataException(message: "metadata qualification requires the complete release inventory");
        }
        await archive.VerifyAsync(
            cancellationToken: token,
            manifest: source
        ).ConfigureAwait(continueOnCapturedContext: false);
        await archive.VerifyAsync(
            cancellationToken: token,
            manifest: target
        ).ConfigureAwait(continueOnCapturedContext: false);
        var storage = new DirectoryObjectStorageTarget(
            Path.Combine(
                path1: directory,
                path2: "store"
            ),
            maximumBlobBytes: WorldReleaseFixtureArchive.MaximumCheckpointBytes
        );
        var authority = new WorldAuthorityBlobStore(
            store: blobs,
            target: storage
        );
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: blobs,
            target: storage
        );
        var operation = Guid.NewGuid();
        var created = Require(outcome: await groups.CreateAsync(
            ("qualification-metadata-" + operation.ToString(format: "N")),
            source.Identity,
            token
        ).ConfigureAwait(continueOnCapturedContext: false));
        var begun = Require(outcome: await groups.BeginAsync(
            created,
            operation,
            target.Identity,
            token
        ).ConfigureAwait(continueOnCapturedContext: false));
        var roots = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var row in definition.Worlds) {
            var identity = new WorldAuthorityIdentity(
                Owner: row.Owner,
                World: row.World
            );
            var root = (await authority.CaptureRecoveryRootAsync(
                cancellationToken: token,
                identity: identity,
                operationId: operation
            ).ConfigureAwait(continueOnCapturedContext: false)
                ?? throw new InvalidDataException(message: $"metadata qualification has no checkpointed authority for '{row.World}'"));

            if (
                (root.Root.FenceToken != Guid.Empty) ||
                (root.Root.CheckpointHash is null) ||
                (root.Root.JournalEntryCount != 0) ||
                (root.Root.JournalSequence != root.Root.CheckpointCoverageSequence)
            ) {
                throw new InvalidDataException(message: $"metadata qualification requires a drained checkpoint for '{row.World}'");
            }
            roots.Add(
                key: $"{row.Owner:D}/{row.World}",
                value: root.Pin
            );
        }
        var coordinator = new WorldReleaseCoordinator(groups: groups);
        var drained = Require(outcome: await coordinator.RecordDrainAsync(
            cancellationToken: token,
            current: begun,
            recoveryRoots: roots
        ).ConfigureAwait(continueOnCapturedContext: false));
        var activation = Require(outcome: await coordinator.RecordActivationAsync(
            cancellationToken: token,
            current: drained
        ).ConfigureAwait(continueOnCapturedContext: false));
        var machines = CliWorldVocabulary.EnsureInstalled();

        foreach (var row in definition.Worlds) {
            var key = $"{row.Owner:D}/{row.World}";

            if (source.Definitions[key] == target.Definitions[key]) { continue; }
            var applied = await authority.PrepareReleaseMetadataAsync(
                new(
                    Owner: row.Owner,
                    World: row.World
                ),
                activation.Record,
                source,
                target,
                archive,
                token,
                machines
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!applied.Ok) { throw new InvalidDataException(message: $"metadata qualification refused '{row.World}': {applied.Detail}"); }
        }
    }
}
