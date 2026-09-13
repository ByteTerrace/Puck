using Puck.Storage;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>Applies the real release publisher to a disposable qualification copy. Both packaged engines
/// subsequently import this same copy; reversal uses the candidate's drained continuation, never the seed.</summary>
internal sealed class WorldReleaseQualificationTransition(IObjectBlobStore blobs, WorldReleaseArchive archive) {
    public async Task ApplyAsync(string directory, WorldSiloDefinition definition, WorldReleaseManifest source,
        WorldReleaseManifest target, CancellationToken token) {
        if (!WorldReleaseCompatibility.TryCheckStructuralCompatibility(source, target, out var reason) ||
            !WorldReleaseCompatibility.TryRequireMetadataCoordinator(source, target, out reason)) {
            throw new InvalidDataException(reason);
        }
        var owner = definition.Worlds.Select(row => row.Owner).Distinct().Single();
        if (!definition.Worlds.Select(row => $"{row.Owner:D}/{row.World}").ToHashSet(StringComparer.Ordinal).SetEquals(source.Definitions.Keys)) {
            throw new InvalidDataException("metadata qualification requires the complete release inventory");
        }
        await archive.VerifyAsync(source, token).ConfigureAwait(false);
        await archive.VerifyAsync(target, token).ConfigureAwait(false);
        var storage = new DirectoryObjectStorageTarget(Path.Combine(directory, "store"), maximumBlobBytes: WorldReleaseFixtureArchive.MaximumCheckpointBytes);
        var authority = new WorldAuthorityBlobStore(blobs, storage);
        var groups = new WorldReleaseGroupStore(blobs, storage, owner);
        var operation = Guid.NewGuid();
        var created = Require(await groups.CreateAsync("qualification-metadata-" + operation.ToString("N"), source.Identity, token).ConfigureAwait(false));
        var begun = Require(await groups.BeginAsync(created, operation, target.Identity, token).ConfigureAwait(false));
        var roots = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in definition.Worlds) {
            var identity = new WorldAuthorityIdentity(row.Owner, row.World);
            var root = await authority.CaptureRecoveryRootAsync(identity, operation, token).ConfigureAwait(false)
                ?? throw new InvalidDataException($"metadata qualification has no checkpointed authority for '{row.World}'");
            if (root.Root.FenceToken != Guid.Empty || root.Root.CheckpointHash is null || root.Root.JournalEntryCount != 0 ||
                root.Root.JournalSequence != root.Root.CheckpointCoverageSequence) {
                throw new InvalidDataException($"metadata qualification requires a drained checkpoint for '{row.World}'");
            }
            roots.Add($"{row.Owner:D}/{row.World}", root.Pin);
        }
        var coordinator = new WorldReleaseCoordinator(groups);
        var drained = Require(await coordinator.RecordDrainAsync(begun, roots, token).ConfigureAwait(false));
        var activation = Require(await coordinator.RecordActivationAsync(drained, token).ConfigureAwait(false));
        var machines = CliWorldVocabulary.EnsureInstalled();
        foreach (var row in definition.Worlds) {
            var key = $"{row.Owner:D}/{row.World}";
            if (source.Definitions[key] == target.Definitions[key]) { continue; }
            var applied = await authority.PrepareReleaseMetadataAsync(new(row.Owner, row.World), activation.Record,
                source, target, archive, token, machines).ConfigureAwait(false);
            if (!applied.Ok) { throw new InvalidDataException($"metadata qualification refused '{row.World}': {applied.Detail}"); }
        }
    }

    private static WorldReleaseGroupSnapshot Require(WorldReleaseGroupOutcome outcome) => outcome.Ok && outcome.Snapshot is { } snapshot
        ? snapshot : throw new InvalidDataException("metadata qualification preparation failed: " + outcome.Detail);
}
