using System.Text.Json;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>A verified recovery point and its retained executable release, ready for an explicit operator decision.</summary>
public sealed record WorldReleaseRestorePreview(WorldReleaseFixtureManifest Point, WorldReleaseManifest Release,
    IReadOnlyDictionary<string, ulong> CurrentDurableTicks);

/// <summary>Validates and applies intentional group rewind through the ordinary private release transaction.
/// Rewind discards gameplay, while current receipts and authority generations remain monotonic.</summary>
public sealed class WorldReleaseRestore(IObjectBlobStore blobs, ObjectStorageTarget target, Guid owner) {
    private readonly WorldAuthorityBlobStore m_authority = new(blobs, target);
    private readonly WorldReleaseArchive m_releases = new(blobs, target, owner);
    private readonly WorldReleaseFixtureArchive m_points = new(blobs, target, owner);
    private readonly WorldReleaseGroupStore m_groups = new(blobs, target, owner);

    /// <summary>Reads every checkpoint, receipt graph and retained artifact, and checks the continuing boundary.
    /// Missing proof is a refusal before admission changes.</summary>
    public async Task<WorldReleaseRestorePreview> InspectAsync(string group, Guid pointId, CancellationToken token = default) {
        var point = await m_points.LoadAsync(pointId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("recovery point is missing or its capture did not complete");
        if (point.Group != group || point.Owner != owner || point.CapturedAt is null || point.RewindBoundary is null) {
            throw new InvalidOperationException("this capture has no durable closed-group rewind proof");
        }
        var state = await m_groups.LoadAsync(group, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("managed group is missing");
        var release = await m_releases.LoadAsync(point.Release, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("recovery point release is not retained");
        var active = await m_releases.LoadAsync(state.Record.ActiveRelease!, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("active release is not retained");
        var keys = point.Worlds.Keys.Select(world => $"{owner:D}/{world}").ToHashSet(StringComparer.Ordinal);
        if (release.CoordinatorContract != WorldReleaseManifest.CurrentCoordinatorContract ||
            active.CoordinatorContract != WorldReleaseManifest.CurrentCoordinatorContract ||
            !keys.SetEquals(release.Definitions.Keys) || !keys.SetEquals(active.Definitions.Keys)) {
            throw new InvalidOperationException("rewind requires the exact complete inventory and releases enforcing its boundary");
        }
        await m_releases.VerifyAsync(release, token).ConfigureAwait(false);
        var ticks = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
        var checkpoints = new List<WorldAuthorityCheckpoint>();
        var authorities = new HashSet<string>(point.Worlds.Keys, StringComparer.Ordinal);
        foreach (var row in point.Worlds) {
            var identity = new WorldAuthorityIdentity(owner, SafeName.Parse(row.Key));
            var current = await m_authority.LoadRootAsync(identity, token).ConfigureAwait(false)
                ?? throw new InvalidDataException($"'{row.Key}' authority is missing");
            var history = await m_points.ReadReceiptsAsync(point, row.Key, token).ConfigureAwait(false);
            var key = $"{owner:D}/{row.Key}";
            var definition = await m_releases.ReadFileAsync(release, release.DefinitionFiles[key], token).ConfigureAwait(false);
            if (current.Root.RewindBoundary != point.RewindBoundary || history.Source.Root.RewindBoundary != point.RewindBoundary ||
                current.Root.Sequence < history.Source.Root.Sequence ||
                history.Source.Root.DefinitionHash != WorldDefinitionFileSource.ComputeContentHash(definition.Span)) {
                throw new InvalidOperationException($"'{row.Key}' no longer proves the recovery point's uninterrupted boundary");
            }
            var bytes = await m_points.ReadCheckpointAsync(point, row.Key, token).ConfigureAwait(false);
            if (!WorldAuthorityCheckpointCodec.TryDecode(bytes.Span, out var checkpoint, out var reason) ||
                checkpoint!.Server.LastCompletedTick != row.Value.Tick) {
                throw new InvalidDataException($"'{row.Key}' recovery checkpoint is invalid: {reason}");
            }
            checkpoints.Add(checkpoint);
            using var document = JsonDocument.Parse(checkpoint.Server.DefinitionJson);
            if (document.RootElement.TryGetProperty("host", out var host) && host.TryGetProperty("authority", out var authority) &&
                authority.ValueKind == JsonValueKind.String && authority.GetString() is { Length: > 0 } name) { authorities.Add(name); }
            ticks.Add(row.Key, current.Root.DurableTick);
        }
        foreach (var checkpoint in checkpoints) { WorldReleaseRewindBoundary.RequireContained(checkpoint, authorities.Contains); }
        return new(point, release, ticks);
    }

    /// <summary>Claims the exact inspected point, leaving the source admitted until the normal Drain phase.</summary>
    public async Task<WorldReleaseGroupOutcome> BeginAsync(string group, Guid pointId, string pointIdentity,
        Guid operationId, bool discardProgress, CancellationToken token = default) {
        if (!discardProgress) { throw new InvalidOperationException("restore requires explicit acknowledgement that player progress will be discarded"); }
        var preview = await InspectAsync(group, pointId, token).ConfigureAwait(false);
        if (preview.Point.Identity != pointIdentity) { throw new InvalidOperationException("the selected recovery point changed"); }
        var current = await m_groups.LoadAsync(group, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("managed group disappeared");
        return await m_groups.BeginRestoreAsync(current, operationId, preview.Point, discardProgress, token).ConfigureAwait(false);
    }

    /// <summary>Applies every selected checkpoint while the operation owns private Activate. Retained current
    /// roots permit ordinary pre-commit recovery; repeated writes are read-only after their restore receipts exist.</summary>
    public async Task ApplyAsync(WorldReleaseGroupRecord operation, CancellationToken token = default) {
        var selection = operation.RestorePoint ?? throw new InvalidOperationException("operation has no intentional restore selection");
        var preview = await InspectAsync(operation.DeploymentGroup, selection.PointId, token).ConfigureAwait(false);
        if (preview.Point.Identity != selection.Identity || preview.Release.Identity != operation.PendingTargetRelease) {
            throw new InvalidDataException("restore selection does not match its retained release");
        }
        foreach (var row in preview.Point.Worlds) {
            var identity = new WorldAuthorityIdentity(owner, SafeName.Parse(row.Key));
            var checkpoint = await m_points.ReadCheckpointAsync(preview.Point, row.Key, token).ConfigureAwait(false);
            var definition = await m_releases.ReadFileAsync(preview.Release, preview.Release.DefinitionFiles[$"{owner:D}/{row.Key}"], token).ConfigureAwait(false);
            var result = await m_authority.PrepareIntentionalRestoreAsync(identity, operation, preview.Point,
                definition, checkpoint, token).ConfigureAwait(false);
            if (!result.Ok) { throw new InvalidOperationException($"'{row.Key}' restore refused: {result.Detail}"); }
        }
    }
}
