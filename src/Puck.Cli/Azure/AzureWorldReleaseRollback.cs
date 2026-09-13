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
            if (current.Record.PendingOperationId is { } pending) {
                throw new InvalidOperationException($"operation {pending:D} is already pending; run 'puck world release resume'");
            }
            if (!current.Record.RollbackEligible || current.Record.ActiveRelease is not { } activeIdentity ||
                current.Record.PreviousRelease is not { } previousIdentity) {
                throw new InvalidOperationException("the deployment has no eligible previous release; a finalized window cannot be reopened by rollback");
            }
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
}
