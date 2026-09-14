using System.Text.Json;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>A verified recovery point and its retained executable release, ready for an explicit operator decision.</summary>
public sealed record WorldReleaseRestorePreview(WorldReleaseFixtureManifest Point, WorldReleaseManifest Release,
    IReadOnlyDictionary<string, ulong> CurrentDurableTicks);
/// <summary>Validates and applies intentional group rewind through the ordinary private release transaction.
/// Rewind discards gameplay, while current receipts and authority generations remain monotonic.</summary>
public sealed class WorldReleaseRestore(IObjectBlobStore blobs, ObjectStorageTarget target, Guid owner) {
    private readonly WorldAuthorityBlobStore m_authority = new(
        store: blobs,
        target: target
    );
    private readonly WorldReleaseArchive m_releases = new(
        blobs,
        target,
        owner
    );
    private readonly WorldReleaseFixtureArchive m_points = new(
        owner: owner,
        store: blobs,
        target: target
    );
    private readonly WorldReleaseGroupStore m_groups = new(
        owner: owner,
        store: blobs,
        target: target
    );

    /// <summary>Applies every selected checkpoint while the operation owns private Activate. Retained current
    /// roots permit ordinary pre-commit recovery; repeated writes are read-only after their restore receipts exist.</summary>
    public async Task ApplyAsync(WorldReleaseGroupRecord operation, CancellationToken token = default) {
        var selection = (operation.RestorePoint ?? throw new InvalidOperationException(message: "operation has no intentional restore selection"));
        var preview = await InspectAsync(
            group: operation.DeploymentGroup,
            pointId: selection.PointId,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (preview.Point.Identity != selection.Identity) ||
            (preview.Release.Identity != operation.PendingTargetRelease)
        ) {
            throw new InvalidDataException(message: "restore selection does not match its retained release");
        }
        foreach (var row in preview.Point.Worlds) {
            var identity = new WorldAuthorityIdentity(
                Owner: owner,
                World: SafeName.Parse(candidate: row.Key)
            );
            var checkpoint = await m_points.ReadCheckpointAsync(
                preview.Point,
                row.Key,
                token
            ).ConfigureAwait(continueOnCapturedContext: false);
            var definition = await m_releases.ReadFileAsync(
                preview.Release,
                preview.Release.DefinitionFiles[$"{owner:D}/{row.Key}"],
                token
            ).ConfigureAwait(continueOnCapturedContext: false);
            var result = await m_authority.PrepareIntentionalRestoreAsync(
                identity,
                operation,
                preview.Point,
                definition,
                checkpoint,
                token
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!result.Ok) { throw new InvalidOperationException(message: $"'{row.Key}' restore refused: {result.Detail}"); }
        }
    }
    /// <summary>Claims the exact inspected point, leaving the source admitted until the normal Drain phase.</summary>
    public async Task<WorldReleaseGroupOutcome> BeginAsync(string group, Guid pointId, string pointIdentity,
        Guid operationId, bool discardProgress, CancellationToken token = default) {
        if (!discardProgress) { throw new InvalidOperationException(message: "restore requires explicit acknowledgement that player progress will be discarded"); }
        var preview = await InspectAsync(
            group: group,
            pointId: pointId,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (preview.Point.Identity != pointIdentity) { throw new InvalidOperationException(message: "the selected recovery point changed"); }
        var current = (await m_groups.LoadAsync(
            cancellationToken: token,
            deploymentGroup: group
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidOperationException(message: "managed group disappeared"));

        return await m_groups.BeginRestoreAsync(
            current,
            operationId,
            preview.Point,
            discardProgress,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Reads every checkpoint, receipt graph and retained artifact, and checks the continuing boundary.
    /// Missing proof is a refusal before admission changes.</summary>
    public async Task<WorldReleaseRestorePreview> InspectAsync(string group, Guid pointId, CancellationToken token = default) {
        var point = (await m_points.LoadAsync(
            cancellationToken: token,
            requestId: pointId
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidDataException(message: "recovery point is missing or its capture did not complete"));

        if (
            (point.Group != group) ||
            (point.Owner != owner) ||
            (point.CapturedAt is null) ||
            (point.RewindBoundary is null)
        ) {
            throw new InvalidOperationException(message: "this capture has no durable closed-group rewind proof");
        }
        var state = (await m_groups.LoadAsync(
            cancellationToken: token,
            deploymentGroup: group
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidOperationException(message: "managed group is missing"));
        var release = (await m_releases.LoadAsync(
            point.Release,
            token
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidDataException(message: "recovery point release is not retained"));
        var active = (await m_releases.LoadAsync(
            state.Record.ActiveRelease!,
            token
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidDataException(message: "active release is not retained"));
        var keys = point.Worlds.Keys.Select(selector: world => $"{owner:D}/{world}").ToHashSet(comparer: StringComparer.Ordinal);

        if (
            (release.CoordinatorContract != WorldReleaseManifest.CurrentCoordinatorContract) ||
            (active.CoordinatorContract != WorldReleaseManifest.CurrentCoordinatorContract) ||
            !keys.SetEquals(other: release.Definitions.Keys) ||
            !keys.SetEquals(other: active.Definitions.Keys)
        ) {
            throw new InvalidOperationException(message: "rewind requires the exact complete inventory and releases enforcing its boundary");
        }
        await m_releases.VerifyAsync(
            cancellationToken: token,
            manifest: release
        ).ConfigureAwait(continueOnCapturedContext: false);
        var ticks = new SortedDictionary<string, ulong>(comparer: StringComparer.Ordinal);
        var checkpoints = new List<WorldAuthorityCheckpoint>();
        var authorities = new HashSet<string>(
            collection: point.Worlds.Keys,
            comparer: StringComparer.Ordinal
        );

        foreach (var row in point.Worlds) {
            var identity = new WorldAuthorityIdentity(
                Owner: owner,
                World: SafeName.Parse(candidate: row.Key)
            );
            var current = (await m_authority.LoadRootAsync(
                cancellationToken: token,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false)
                ?? throw new InvalidDataException(message: $"'{row.Key}' authority is missing"));
            var history = await m_points.ReadReceiptsAsync(
                point,
                row.Key,
                token
            ).ConfigureAwait(continueOnCapturedContext: false);
            var key = $"{owner:D}/{row.Key}";
            var definition = await m_releases.ReadFileAsync(
                release,
                release.DefinitionFiles[key],
                token
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (current.Root.RewindBoundary != point.RewindBoundary) ||
                (history.Source.Root.RewindBoundary != point.RewindBoundary) ||
                (current.Root.Sequence < history.Source.Root.Sequence) ||
                (history.Source.Root.DefinitionHash != WorldDefinitionFileSource.ComputeContentHash(content: definition.Span))
            ) {
                throw new InvalidOperationException(message: $"'{row.Key}' no longer proves the recovery point's uninterrupted boundary");
            }
            var bytes = await m_points.ReadCheckpointAsync(
                point,
                row.Key,
                token
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                !WorldAuthorityCheckpointCodec.TryDecode(
                bytes: bytes.Span,
                checkpoint: out var checkpoint,
                reason: out var reason
            ) ||
                (checkpoint!.Server.LastCompletedTick != row.Value.Tick)
            ) {
                throw new InvalidDataException(message: $"'{row.Key}' recovery checkpoint is invalid: {reason}");
            }
            checkpoints.Add(item: checkpoint);
            using var document = JsonDocument.Parse(checkpoint.Server.DefinitionJson);

            if (
                document.RootElement.TryGetProperty(
                propertyName: "host",
                value: out var host
            ) &&
                host.TryGetProperty(
                propertyName: "authority",
                value: out var authority
            ) &&
                (authority.ValueKind == JsonValueKind.String) &&
                (authority.GetString() is { Length: > 0 } name)
            ) { authorities.Add(item: name); }
            ticks.Add(
                key: row.Key,
                value: current.Root.DurableTick
            );
        }
        foreach (var checkpoint in checkpoints) { WorldReleaseRewindBoundary.RequireContained(
            checkpoint: checkpoint,
            containsAuthority: authorities.Contains
        ); }
        return new(
            CurrentDurableTicks: ticks,
            Point: point,
            Release: release
        );
    }
}
