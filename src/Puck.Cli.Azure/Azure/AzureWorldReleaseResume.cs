using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    /// <summary>Resumes only the durable pending operation, using its retained image, template, and secret versions.</summary>
    internal static Task<WorldReleaseRunResult> ResumeWorldReleaseAsync(TimeProvider clock, CancellationToken cancellationToken) =>
        WithManagedWorldReleaseAsync(
            action: ResumeWorldReleaseCoreAsync,
            cancellationToken: cancellationToken,
            clock: clock
        );

    private static async Task<WorldReleaseRunResult> ResumeWorldReleaseCoreAsync(WorldReleaseContext context, CancellationToken token) {
        var state = (await context.Groups.LoadAsync(
            context.Group,
            token
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidOperationException(message: "no managed deployment group exists"));

        if (state.Record.PendingOperationId is null) {
            return new(
                CandidatePrivate: false,
                Completed: true,
                Detail: "no pending release operation",
                Snapshot: state
            );
        }
        // Staging holds nothing a later look needs, so it is deleted however the step ends.
        using var temporary = RunDirectory.Create(
            keepOnFailure: false,
            prefix: "puck-release-resume-"
        );
        var (candidate, runtime) = await CreateWorldReleaseRuntimeAsync(
            context: context,
            record: state.Record,
            temporary: temporary.Path,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);

        return await new WorldReleaseCoordinator(groups: context.Groups).ResumeAsync(
            state,
            candidate,
            runtime,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    // Ends the group's unfinished operation of another release (WorldReleaseCoordinator.SupersedeAsync) and returns the
    // group state that holds no unfinished operation, or refuses with the reason it could not end.
    private static async Task<WorldReleaseGroupSnapshot> SupersedeWorldReleaseAsync(WorldReleaseContext context,
        WorldReleaseGroupSnapshot state, string successor, CancellationToken token) {
        using var temporary = RunDirectory.Create(
            keepOnFailure: false,
            prefix: "puck-release-supersede-"
        );
        var (candidate, runtime) = await CreateWorldReleaseRuntimeAsync(
            context: context,
            record: state.Record,
            temporary: temporary.Path,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);
        var result = await new WorldReleaseCoordinator(groups: context.Groups).SupersedeAsync(
            state,
            successor,
            candidate,
            await runtime.FindCapturedRootsAsync(
                cancellationToken: token,
                operationId: state.Record.PendingOperationId!.Value
            ).ConfigureAwait(continueOnCapturedContext: false),
            runtime,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);

        Console.WriteLine(value: $"Superseded operation {state.Record.PendingOperationId:D}: {result.Detail}");
        return ((result.Snapshot is { Record.HasUnfinishedOperation: false } ended)
            ? ended
            : throw new InvalidOperationException(message: $"the unfinished operation {state.Record.PendingOperationId:D} could not be superseded: {result.Detail}"));
    }
    // The Azure runtime driver for the record's pending operation, over its retained source and target deployments.
    private static async Task<(WorldReleaseManifest Candidate, AzureWorldReleaseRuntime Runtime)> CreateWorldReleaseRuntimeAsync(
        WorldReleaseContext context, WorldReleaseGroupRecord record, string temporary, CancellationToken token) {
        var candidate = await LoadWorldReleaseDeploymentAsync(
            context: context,
            identity: record.PendingTargetRelease!,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);
        var source = ((record.PendingSourceRelease is { } sourceIdentity)
            ? await LoadWorldReleaseDeploymentAsync(
                context: context,
                identity: sourceIdentity,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false)
            : null
        );
        return (candidate.Manifest, new AzureWorldReleaseRuntime(
            context.ResourceGroup,
            context.Group,
            temporary,
            context.Groups,
            context.Authority,
            context.Archive,
            new WorldReleaseRestore(
                context.Blobs,
                context.Target,
                context.Owner,
                context.Clock,
                LocalWorldsDirectory
            ),
            source,
            candidate,
            ct => WorldReleaseBootstrap.PublishAsync(
                candidate.Manifest,
                context.Archive,
                context.Authority,
                context.Owner,
                LocalWorldsDirectory,
                record.History,
                ct
            ),
            context.Clock
        ));
    }
}
