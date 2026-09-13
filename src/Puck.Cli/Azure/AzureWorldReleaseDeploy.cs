using System.Text.Json;
using Puck.Cli.Automation;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Retains and qualifies an exact package before entering the durable maintenance transaction.</summary>
    internal static async Task<WorldReleaseRunResult> DeployWorldReleaseAsync(string packageDirectory,
        Guid? operationId, CancellationToken cancellationToken, string? resourceGroup = null) {
        if (operationId == Guid.Empty) {
            throw new ArgumentException(
                message: "release operation ID must not be empty",
                paramName: nameof(operationId)
            );
        }
        packageDirectory = Path.GetFullPath(path: packageDirectory);
        var manifest = (JsonSerializer.Deserialize<WorldReleaseManifest>(ConfinedFile.ReadAllBytes(
            Path.Combine(
                path1: packageDirectory,
                path2: "release.json"
            ),
            (1024 * 1024)
        ))
            ?? throw new InvalidDataException(message: "release package contains no manifest"));

        if (!WorldReleaseManifest.TryVerify(
            manifest: manifest,
            packageDirectory: packageDirectory,
            reason: out var reason
        )) { throw new InvalidDataException(message: reason); }
        if (manifest.CoordinatorContract != WorldReleaseManifest.CurrentCoordinatorContract) {
            throw new InvalidDataException(message: "new official deployments require the closed-group restore coordinator contract");
        }
        return await WithManagedWorldReleaseAsync(
            action: async (context, token) => {
                var current = await context.Groups.LoadAsync(
                    context.Group,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (
                    (current is { } pending) &&
                    (pending.Record.PendingOperationId is { } existingOperation)
                ) {
                    if (
                        (pending.Record.PendingTargetRelease != manifest.Identity) ||
                        ((operationId is { } requested) && (requested != existingOperation))
                    ) {
                        throw new InvalidOperationException(message: $"operation {existingOperation:D} is already pending; run 'puck world release resume'");
                    }
                    return await ResumeWorldReleaseCoreAsync(
                        context: context,
                        token: token
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
                if (current?.Record.ActiveRelease == manifest.Identity) {
                    var active = await LoadWorldReleaseDeploymentAsync(
                        context: context,
                        identity: manifest.Identity,
                        token: token
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    await TestWorldReleaseAsync(
                        group: context.ResourceGroup,
                        image: active.Image,
                        scaleSet: context.Group
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return new WorldReleaseRunResult(
                        CandidatePrivate: false,
                        Completed: true,
                        Detail: "the exact release is already active and healthy",
                        Snapshot: current
                    );
                }
                if (current?.Record.RollbackEligible == true) {
                    throw new InvalidOperationException(message: "the previous release is still retained for rollback; finalize that window before deploying another release");
                }
                if (current is null) {
                    if ((await WorkersAsync(
                        group: context.ResourceGroup,
                        scaleSet: context.Group
                    ).ConfigureAwait(continueOnCapturedContext: false)).Length != 0) {
                        throw new InvalidOperationException(message: "an existing unmanaged worker cannot be adopted as an empty bootstrap");
                    }
                    var created = await context.Groups.CreateAsync(
                        context.Group,
                        null,
                        token
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    if (!created.Ok) { throw new InvalidOperationException(message: created.Detail); }
                    current = created.Snapshot;
                }
                var state = current!.Value;
                AzureWorldReleaseDeployment? source = null;

                if (state.Record.ActiveRelease is { } sourceIdentity) {
                    source = await LoadWorldReleaseDeploymentAsync(
                        context: context,
                        identity: sourceIdentity,
                        token: token
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    if (!WorldReleaseTransitionPolicy.TryPrepare(
                        source.Manifest,
                        manifest,
                        out _,
                        out reason
                    )) { throw new InvalidDataException(message: reason); }
                }
                // Reuse already retained inputs on retry. Regenerating SSH material or reading today's template would
                // otherwise turn a lost preflight response into a conflicting configuration for the same release.
                await context.Archive.SaveAsync(
                    cancellationToken: token,
                    manifest: manifest,
                    packageDirectory: packageDirectory
                ).ConfigureAwait(continueOnCapturedContext: false);
                var retained = await context.Deployments.LoadAsync(
                    manifest,
                    context.Group,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (retained is null) {
                    retained = await PrepareWorldReleaseDeploymentAsync(
                        manifest,
                        context.ResourceGroup
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    await context.Deployments.SaveAsync(
                        cancellationToken: token,
                        configuration: retained,
                        manifest: manifest
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
                var candidate = await LoadWorldReleaseDeploymentAsync(
                    context: context,
                    identity: manifest.Identity,
                    token: token
                ).ConfigureAwait(continueOnCapturedContext: false);

                await RetainWorldReleaseImagesAsync(
                    ((source is null)
                    ? [candidate.Image]
                    : [source.Image, candidate.Image]),
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);
                var operation = (operationId ?? Guid.NewGuid());

                Console.WriteLine(value: $"Release operation: {operation:D}");
                var fixtureDirectory = await PrepareWorldReleaseFixtureAsync(
                    bootstrap: manifest,
                    context: context,
                    requestId: operation,
                    source: source,
                    token: token
                ).ConfigureAwait(continueOnCapturedContext: false);
                var runner = new WorldReleaseQualificationRunner(
                    fixtureDirectory,
                    Path.GetFullPath(path: "artifacts/world-release-qualification"),
                    (source?.Image ?? candidate.Image),
                    candidate.Image,
                    archive: context.Archive
                );
                var coordinator = new WorldReleaseCoordinator(groups: context.Groups);
                var begun = ((source is null)
                    ? await coordinator.BeginBootstrapAsync(
                        cancellationToken: token,
                        current: state,
                        operationId: operation,
                        qualificationRunner: runner,
                        target: manifest
                    ).ConfigureAwait(continueOnCapturedContext: false)
                    : await coordinator.BeginDeploymentAsync(
                        state,
                        source.Manifest,
                        manifest,
                        runner,
                        operation,
                        token
                    ).ConfigureAwait(continueOnCapturedContext: false)
                );

                if (!begun.Ok) { throw new InvalidOperationException(message: begun.Detail); }
                return await ResumeWorldReleaseCoreAsync(
                    context: context,
                    token: token
                ).ConfigureAwait(continueOnCapturedContext: false);
            },
            cancellationToken: cancellationToken,
            resourceGroup: resourceGroup
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static async Task DeployWorldAsync(string? group, string package, Guid? operation, CancellationToken token) {
        var result = await DeployWorldReleaseAsync(
            cancellationToken: token,
            operationId: operation,
            packageDirectory: package,
            resourceGroup: group
        ).ConfigureAwait(continueOnCapturedContext: false);

        Console.WriteLine(value: result.Detail);
        if (
            !result.Completed ||
            result.SourceRecovered
        ) { throw new InvalidOperationException(message: "release did not complete; inspect 'puck world release status' and resume its durable operation"); }
    }
}
