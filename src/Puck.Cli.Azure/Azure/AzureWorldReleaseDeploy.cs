using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    /// <summary>Retains and qualifies an exact package before entering the durable maintenance transaction.</summary>
    internal static async Task<WorldReleaseRunResult> DeployWorldReleaseAsync(string packageDirectory,
        Guid? operationId, TimeProvider clock, CancellationToken cancellationToken) {
        if (operationId == Guid.Empty) {
            throw new ArgumentException(
                message: "release operation ID must not be empty",
                paramName: nameof(operationId)
            );
        }
        packageDirectory = Path.GetFullPath(path: packageDirectory);
        var manifest = WorldReleaseManifest.ReadVerified(packageDirectory: packageDirectory);

        return await WithManagedWorldReleaseAsync(
            action: async (context, token) => {
                var policy = WorldHostedDataPolicyOf(outputs: context.Outputs);

                Console.WriteLine(value: $"Hosted data policy: {policy}");
                if (policy == WorldHostedDataPolicy.Scratch) {
                    return await ReplaceWorldReleaseAsync(
                        context: context,
                        manifest: manifest,
                        operationId: operationId,
                        packageDirectory: packageDirectory,
                        policy: policy,
                        token: token
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
                var current = await context.Groups.LoadAsync(
                    context.Group,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                switch (WorldReleaseCoordinator.Succession(
                    operationId: operationId,
                    record: current?.Record,
                    release: manifest.Identity
                )) {
                    case WorldReleaseSuccession.Resume:
                        return await ResumeWorldReleaseCoreAsync(
                            context: context,
                            token: token
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    case WorldReleaseSuccession.Supersede:
                        current = await SupersedeWorldReleaseAsync(
                            context: context,
                            state: current!.Value,
                            successor: manifest.Identity,
                            token: token
                        ).ConfigureAwait(continueOnCapturedContext: false);
                        break;
                }
                if (current?.Record.ActiveRelease == manifest.Identity) {
                    return await TestActiveWorldReleaseAsync(
                        context: context,
                        current: current.Value,
                        manifest: manifest
                    ).ConfigureAwait(continueOnCapturedContext: false);
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
                    // A pair that cannot carry state structurally is not refused here: hosted data is scratch, so the
                    // fixture's reset decision (WorldReleaseCoordinator.FindDefinitionResetAsync) starts it fresh.
                    if (
                        !WorldReleaseManifest.TryValidate(manifest: source.Manifest, reason: out var reason) ||
                        !WorldReleaseManifest.TryValidate(manifest: manifest, reason: out reason)
                    ) { throw new InvalidDataException(message: reason); }
                }
                var candidate = await RetainWorldReleaseDeploymentAsync(
                    context: context,
                    manifest: manifest,
                    packageDirectory: packageDirectory,
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
                var (fixtureDirectory, reset) = await PrepareWorldReleaseFixtureAsync(
                    bootstrap: manifest,
                    context: context,
                    requestId: operation,
                    source: source,
                    successor: manifest,
                    token: token
                ).ConfigureAwait(continueOnCapturedContext: false);
                // A reset qualifies the candidate alone from its own fresh state, as a bootstrap does.
                var runner = new WorldReleaseQualificationRunner(
                    fixtureDirectory,
                    Path.GetFullPath(path: "artifacts/world-release-qualification"),
                    (((source is null) || (reset is not null)) ? candidate.Image : source.Image),
                    candidate.Image,
                    archive: context.Archive,
                    clock: context.Clock,
                    documentDirectory: LocalWorldsDirectory
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
                    : (reset is not null)
                        ? await coordinator.BeginResetAsync(
                            state,
                            source.Manifest,
                            manifest,
                            runner,
                            operation,
                            reset,
                            token
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
            clock: clock
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
}
