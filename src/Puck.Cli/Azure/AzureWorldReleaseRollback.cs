using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Qualifies the retained predecessor against current source state, then rolls back through the
    /// ordinary drain and cutover transaction. It never selects an earlier gameplay checkpoint.</summary>
    internal static Task<WorldReleaseRunResult> RollbackWorldReleaseAsync(Guid? operationId, CancellationToken cancellationToken) {
        if (operationId == Guid.Empty) {
            throw new ArgumentException(
                message: "release operation ID must not be empty",
                paramName: nameof(operationId)
            );
        }
        return WithManagedWorldReleaseAsync(
            async (context, token) => {
                var current = (await context.Groups.LoadAsync(
                    context.Group,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false)
                    ?? throw new InvalidOperationException(message: "no managed deployment group exists"));

                var (activeIdentity, previousIdentity) = GetWorldReleaseRollbackPair(
                    current.Record,
                    operationId
                );
                var active = await LoadWorldReleaseDeploymentAsync(
                    context: context,
                    identity: activeIdentity,
                    token: token
                ).ConfigureAwait(continueOnCapturedContext: false);
                var previous = await LoadWorldReleaseDeploymentAsync(
                    context: context,
                    identity: previousIdentity,
                    token: token
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (active.Configuration.ClosedGroupRewind) { RequireClosedRewindDeployment(deployment: previous); }
                if (!WorldReleaseTransitionPolicy.TryPrepare(
                    active.Manifest,
                    previous.Manifest,
                    out _,
                    out var reason
                )) { throw new InvalidDataException(message: reason); }
                await RetainWorldReleaseImagesAsync(
                    [active.Image, previous.Image],
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);
                var operation = (operationId ?? Guid.NewGuid());

                Console.WriteLine(value: $"Rollback operation: {operation:D}");
                var fixture = await PrepareWorldReleaseFixtureAsync(
                    context,
                    active,
                    previous.Manifest,
                    operation,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);
                var runner = new WorldReleaseQualificationRunner(
                    fixture,
                    Path.GetFullPath(path: "artifacts/world-release-qualification"),
                    active.Image,
                    previous.Image,
                    archive: context.Archive
                );
                var begun = await new WorldReleaseCoordinator(groups: context.Groups).BeginRollbackAsync(
                    current,
                    active.Manifest,
                    previous.Manifest,
                    runner,
                    operation,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!begun.Ok) { throw new InvalidOperationException(message: begun.Detail); }
                return await ResumeWorldReleaseCoreAsync(
                    context: context,
                    token: token
                ).ConfigureAwait(continueOnCapturedContext: false);
            },
            cancellationToken
        );
    }
    /// <summary>Selects the retained pair before any cloud effects, distinguishing completed retention from maintenance.</summary>
    internal static (string Active, string Previous) GetWorldReleaseRollbackPair(WorldReleaseGroupRecord record, Guid? operationId = null) {
        if (record.HasUnfinishedOperation) {
            throw new InvalidOperationException(message: $"operation {record.PendingOperationId:D} is already pending; run 'puck world release resume'");
        }
        if (record.Admission != WorldReleaseAdmissionState.Open) {
            throw new InvalidOperationException(message: "the deployment is not admitted; run 'puck world release resume' before rollback");
        }
        if (
            (operationId is { } requested) &&
            record.ContainsOperation(operationId: requested)
        ) {
            throw new InvalidOperationException(message: $"operation {requested:D} already belongs to pending or retained work; inspect 'puck world release status' and use 'resume' for pending work");
        }
        if (
            !record.RollbackEligible ||
            (record.ActiveRelease is not { } active) ||
            (record.PreviousRelease is not { } previous)
        ) {
            throw new InvalidOperationException(message: "the deployment has no eligible previous release; a finalized window cannot be reopened by rollback");
        }
        return (active, previous);
    }
}
