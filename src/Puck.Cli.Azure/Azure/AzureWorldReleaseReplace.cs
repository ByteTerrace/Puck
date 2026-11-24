using System.Text.Json.Nodes;
using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    /// <summary>The deployment output that declares the world group's hosted-data policy
    /// (<see cref="WorldHostedData.ParsePolicy"/>), from <c>worldSiloHostedData</c> in <c>main.bicepparam</c>. An absent
    /// output is durable, so a deployment discards hosted data only when its configuration says scratch.</summary>
    private const string WorldHostedDataOutput = "worldSiloHostedData";

    private static WorldHostedDataPolicy WorldHostedDataPolicyOf(JsonNode outputs) =>
        WorldHostedData.ParsePolicy(value: outputs[WorldHostedDataOutput]?["value"]?.GetValue<string>());

    // Retains a release's exact package and its deployment inputs in the owner's release store, reusing inputs a
    // retry already retained: regenerating SSH material or reading today's template would turn a lost preflight
    // response into a conflicting configuration for the same release.
    private static async Task<AzureWorldReleaseDeployment> RetainWorldReleaseDeploymentAsync(WorldReleaseContext context, WorldReleaseManifest manifest,
        string packageDirectory, CancellationToken token) {
        await context.Archive.SaveAsync(
            cancellationToken: token,
            manifest: manifest,
            packageDirectory: packageDirectory
        ).ConfigureAwait(continueOnCapturedContext: false);
        if (await context.Deployments.LoadAsync(
            manifest,
            context.Group,
            token
        ).ConfigureAwait(continueOnCapturedContext: false) is null) {
            var prepared = await PrepareWorldReleaseDeploymentAsync(
                manifest,
                context.ResourceGroup,
                context.Clock
            ).ConfigureAwait(continueOnCapturedContext: false);

            await context.Deployments.SaveAsync(
                cancellationToken: token,
                configuration: prepared.Configuration,
                keys: prepared.Keys,
                manifest: manifest
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        return await LoadWorldReleaseDeploymentAsync(
            context: context,
            identity: manifest.Identity,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    // A deployment of the release already active only proves it is serving healthily.
    private static async Task<WorldReleaseRunResult> TestActiveWorldReleaseAsync(WorldReleaseContext context, WorldReleaseGroupSnapshot current,
        WorldReleaseManifest manifest, CancellationToken token = default) {
        var active = await LoadWorldReleaseDeploymentAsync(
            context: context,
            identity: manifest.Identity,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);

        await TestWorldReleaseAsync(
            clock: context.Clock,
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
    /// <summary>Deploys a release over scratch hosted data as a first deployment: it qualifies the candidate alone from a
    /// fresh bootstrap fixture while the serving world still runs, then stops the group's compute, discards its hosted
    /// data (<see cref="WorldHostedData.DiscardAsync"/>), and runs the bootstrap transaction CI's qualification
    /// exercises. A qualification failure changes nothing that serves; a failure after the discard leaves the group
    /// without a world until a later deployment succeeds, since there is no source to recover.</summary>
    private static async Task<WorldReleaseRunResult> ReplaceWorldReleaseAsync(WorldReleaseContext context, WorldReleaseManifest manifest,
        string packageDirectory, Guid? operationId, WorldHostedDataPolicy policy, CancellationToken token) {
        WorldReleaseGroupSnapshot? current = null;

        // A record this build cannot read is scratch like the rest: the discard deletes it without reading it.
        try {
            current = await context.Groups.LoadAsync(
                context.Group,
                token
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (InvalidDataException error) {
            Console.WriteLine(value: $"The group record cannot be read and is discarded with the hosted data: {error.Message}");
        }
        if (current is { Record: { HasUnfinishedOperation: false, Admission: WorldReleaseAdmissionState.Open } serving } && (serving.ActiveRelease == manifest.Identity)) {
            return await TestActiveWorldReleaseAsync(
                context: context,
                current: current.Value,
                manifest: manifest,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        var candidate = await RetainWorldReleaseDeploymentAsync(
            context: context,
            manifest: manifest,
            packageDirectory: packageDirectory,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);

        await RetainWorldReleaseImagesAsync(
            [candidate.Image],
            token
        ).ConfigureAwait(continueOnCapturedContext: false);
        var operation = (operationId ?? Guid.NewGuid());

        Console.WriteLine(value: $"Release operation: {operation:D}");
        var (fixtureDirectory, _) = await PrepareWorldReleaseFixtureAsync(
            bootstrap: manifest,
            context: context,
            requestId: operation,
            source: null,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);
        var qualification = await new WorldReleaseQualificationRunner(
            fixtureDirectory,
            Path.GetFullPath(path: "artifacts/world-release-qualification"),
            candidate.Image,
            candidate.Image,
            archive: context.Archive,
            clock: context.Clock,
            documentDirectory: LocalWorldsDirectory
        ).RunAsync(
            cancellationToken: token,
            target: manifest
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (qualification.Failure is { } failure) {
            throw new InvalidOperationException(message: $"the candidate failed its bootstrap qualification, and the serving world was left as it was: {failure}");
        }
        var discarded = await WorldHostedData.DiscardAsync(
            cancellationToken: token,
            deploymentGroup: context.Group,
            owner: context.Owner,
            policy: policy,
            stopWriters: _ => DeleteWorldComputeAsync(
                clock: context.Clock,
                group: context.ResourceGroup,
                scaleSet: context.Group
            ),
            store: context.Blobs,
            target: context.Target
        ).ConfigureAwait(continueOnCapturedContext: false);

        Console.WriteLine(value: $"Replaced the hosted world: stopped {context.Group}'s compute and discarded {discarded.DeletedObjects} objects of {discarded.Worlds.Count} worlds ({string.Join(separator: ", ", values: discarded.Worlds)}){(discarded.GroupRecordDeleted ? " and the group record" : "")}.");
        var created = await context.Groups.CreateAsync(
            context.Group,
            null,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!created.Ok) { throw new InvalidOperationException(message: created.Detail); }
        var begun = await new WorldReleaseCoordinator(groups: context.Groups).BeginBootstrapAsync(
            cancellationToken: token,
            current: created.Snapshot!.Value,
            operationId: operation,
            qualificationRunner: new WorldReleaseBootstrapQualification(qualified: manifest, result: qualification),
            target: manifest
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!begun.Ok) { throw new InvalidOperationException(message: begun.Detail); }
        return await ResumeWorldReleaseCoreAsync(
            context: context,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
}
