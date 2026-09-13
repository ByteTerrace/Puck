using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Qualifies the retained predecessor against current source state, then rolls back through the
    /// ordinary drain and cutover transaction. It never selects an earlier gameplay checkpoint.</summary>
    internal static Task<WorldReleaseRunResult> RollbackWorldReleaseAsync(Guid? operationId, CancellationToken cancellationToken) {
        if (operationId == Guid.Empty) { throw new ArgumentException("release operation ID must not be empty", nameof(operationId)); }
        return WithManagedWorldReleaseAsync(async (context, token) => {
            var current = await context.Groups.LoadAsync(context.Group, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("no managed deployment group exists");
            var (activeIdentity, previousIdentity) = GetWorldReleaseRollbackPair(current.Record, operationId);
            var active = await LoadWorldReleaseDeploymentAsync(context, activeIdentity, token).ConfigureAwait(false);
            var previous = await LoadWorldReleaseDeploymentAsync(context, previousIdentity, token).ConfigureAwait(false);
            if (!WorldReleaseTransitionPolicy.TryPrepare(active.Manifest, previous.Manifest, out _, out var reason)) { throw new InvalidDataException(reason); }
            await RetainWorldReleaseImagesAsync([active.Image, previous.Image], token).ConfigureAwait(false);
            var operation = operationId ?? Guid.NewGuid();
            Console.WriteLine($"Rollback operation: {operation:D}");
            var fixture = await PrepareWorldReleaseFixtureAsync(context, active, previous.Manifest, operation, token).ConfigureAwait(false);
            var runner = new WorldReleaseQualificationRunner(fixture, Path.GetFullPath("artifacts/world-release-qualification"), active.Image, previous.Image);
            var begun = await new WorldReleaseCoordinator(context.Groups).BeginRollbackAsync(current, active.Manifest, previous.Manifest, runner, operation, token).ConfigureAwait(false);
            if (!begun.Ok) { throw new InvalidOperationException(begun.Detail); }
            return await ResumeWorldReleaseCoreAsync(context, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Selects the retained pair before any cloud effects, distinguishing completed retention from maintenance.</summary>
    internal static (string Active, string Previous) GetWorldReleaseRollbackPair(WorldReleaseGroupRecord record, Guid? operationId = null) {
        if (record.HasUnfinishedOperation) {
            throw new InvalidOperationException($"operation {record.PendingOperationId:D} is already pending; run 'puck world release resume'");
        }
        if (record.Admission != WorldReleaseAdmissionState.Open) {
            throw new InvalidOperationException("the deployment is not admitted; run 'puck world release resume' before rollback");
        }
        if (operationId is { } requested && record.ContainsOperation(requested)) {
            throw new InvalidOperationException($"operation {requested:D} already belongs to pending or retained work; inspect 'puck world release status' and use 'resume' for pending work");
        }
        if (!record.RollbackEligible || record.ActiveRelease is not { } active || record.PreviousRelease is not { } previous) {
            throw new InvalidOperationException("the deployment has no eligible previous release; a finalized window cannot be reopened by rollback");
        }
        return (active, previous);
    }
}
