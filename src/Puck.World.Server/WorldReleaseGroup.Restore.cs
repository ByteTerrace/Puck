namespace Puck.World.Server;

public sealed partial class WorldReleaseGroupStore {
    /// <summary>Begins an explicitly acknowledged rewind after its complete point and boundary have been checked.
    /// Admission stays open until the coordinator records Drain. The selected point is immutable across resume.</summary>
    public Task<WorldReleaseGroupOutcome> BeginRestoreAsync(WorldReleaseGroupSnapshot current, Guid operationId,
        WorldReleaseFixtureManifest point, bool discardProgress, CancellationToken cancellationToken = default) {
        var old = current.Record;

        if (
            !discardProgress ||
            (operationId == Guid.Empty) ||
            old.ContainsOperation(operationId: operationId) ||
            old.HasUnfinishedOperation ||
            (old.Admission != WorldReleaseAdmissionState.Open) ||
            (old.ActiveRelease is null) ||
            (point.Owner != old.Owner) ||
            (point.Group != old.DeploymentGroup) ||
            (point.CapturedAt is null) ||
            (point.RewindBoundary != WorldReleaseRewindBoundary.Compute(
            old.Owner,
            old.DeploymentGroup,
            point.Worlds.Keys
        ))
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "restore requires an acknowledged, proven recovery point and an admitted group without unfinished work"
            ));
        }
        var history = ((old.PendingOperationId is { } previous)
            ? old.History.Append(element: new WorldReleaseGroupHistoryEntry {
            OperationId = previous,
            RecoveryRoots = old.RecoveryRoots,
            RestorePoint = old.RestorePoint,
            Result = "committed",
            Revision = old.Revision,
            SourceRelease = old.PendingSourceRelease,
            TargetRelease = old.PendingTargetRelease!,
        }).ToArray()
            : old.History
        );
        var next = old with {
            PendingOperationId = operationId,
            PendingSourceRelease = old.ActiveRelease,
            PendingTargetRelease = point.Release,
            RestorePoint = new(
            point.RequestId,
            point.Identity
        ),
            PendingPhase = WorldReleaseOperationPhase.Prepare,
            PendingCommitted = false,
            PendingFailure = null,
            RecoveryRoots = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal),
            History = history,
            Revision = checked((old.Revision + 1)),
        };

        return WriteAsync(
            cancellationToken: cancellationToken,
            current: current,
            next: next
        );
    }
}
