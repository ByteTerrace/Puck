using System.Text.Json.Nodes;
using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    /// <summary>Reads the world group's hosted-data policy (<see cref="WorldHostedData.ParsePolicy"/>) from the live
    /// deployment: the <c>worldSiloHostedData</c> output (from <c>main.bicepparam</c>) of the latest successful
    /// composition that deploys the world platform, never the local outputs file, which another deployment may have
    /// superseded. No such output is durable, so hosted data is discarded only on a live scratch declaration.</summary>
    private static async Task<WorldHostedDataPolicy> ReadWorldHostedDataPolicyAsync(string resourceGroup) {
        var declared = await AzJsonAsync(
            "deployment",
            "group",
            "list",
            "-g",
            resourceGroup,
            "--query",
            "[?(name=='puck-production-platform' || name=='puck-world-platform') && properties.provisioningState=='Succeeded'] | sort_by(@, &properties.timestamp) | [-1].properties.outputs.worldSiloHostedData || `{}`",
            "-o",
            "json"
        ).ConfigureAwait(continueOnCapturedContext: false);

        return WorldHostedData.ParsePolicy(value: declared["value"]?.GetValue<string>());
    }
    // Stops every writer of the owner's container: the group's own scale set, after proving that no other compute in the
    // resource group runs under the silo identity, which is the only runtime identity granted that container. A scale set
    // or VM of another name with that identity is another group's writer (a renamed scale set an incremental deployment
    // left behind), and the discard refuses rather than delete it or discard under it.
    private static async Task StopWorldSiloWritersAsync(WorldReleaseContext context) {
        var identity = Text(value: Value(
            context.Outputs,
            "worldSiloIdentityResourceId"
        ));
        bool Holds(JsonNode? identities) => (identities is JsonObject assigned) &&
            assigned.Any(predicate: pair => string.Equals(a: pair.Key, b: identity, comparisonType: StringComparison.OrdinalIgnoreCase));
        var strangers = new List<string>();

        foreach (var set in (await AzJsonAsync("vmss", "list", "-g", context.ResourceGroup, "--query", "[].{name:name, identities:identity.userAssignedIdentities}", "-o", "json").ConfigureAwait(continueOnCapturedContext: false)).AsArray()) {
            if (Holds(identities: set!["identities"]) && !string.Equals(a: Text(value: set["name"]), b: context.Group, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                strangers.Add(item: $"scale set {Text(value: set["name"])}");
            }
        }
        foreach (var vm in (await AzJsonAsync("vm", "list", "-g", context.ResourceGroup, "--query", "[].{name:name, identities:identity.userAssignedIdentities, set:virtualMachineScaleSet.id}", "-o", "json").ConfigureAwait(continueOnCapturedContext: false)).AsArray()) {
            var set = vm!["set"]?.GetValue<string>();

            if (Holds(identities: vm["identities"]) && ((set is null) || !set.EndsWith(value: ("/" + context.Group), comparisonType: StringComparison.OrdinalIgnoreCase))) {
                strangers.Add(item: $"VM {Text(value: vm["name"])}");
            }
        }
        if (strangers.Count != 0) {
            throw new InvalidOperationException(message: $"other compute runs under the world silo identity ({string.Join(separator: ", ", values: strangers)}); it may write the hosted data, so nothing was stopped or discarded");
        }
        await DeleteWorldComputeAsync(
            clock: context.Clock,
            group: context.ResourceGroup,
            scaleSet: context.Group
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

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
        string packageDirectory, Guid? operationId, CancellationToken token) {
        WorldReleaseGroupSnapshot? current = null;

        // A record this build cannot read, or one an unfinished discard still holds (LoadAsync refuses it), is scratch
        // like the rest: the discard below deletes it without reading it.
        try {
            current = await context.Groups.LoadAsync(
                context.Group,
                token
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (InvalidDataException error) {
            Console.WriteLine(value: $"The group record cannot be read and is discarded with the hosted data: {error.Message}");
        }
        // The release already serves only while its record is readable, settled and open and its one worker exists; any
        // other state (an interrupted discard stopped the worker) is replaced.
        if (
            (current is { Record: { HasUnfinishedOperation: false, Admission: WorldReleaseAdmissionState.Open } serving }) &&
            (serving.ActiveRelease == manifest.Identity) &&
            ((await WorkersAsync(group: context.ResourceGroup, scaleSet: context.Group).ConfigureAwait(continueOnCapturedContext: false)).Length == 1)
        ) {
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
            readPolicy: _ => ReadWorldHostedDataPolicyAsync(resourceGroup: context.ResourceGroup),
            stopWriters: _ => StopWorldSiloWritersAsync(context: context),
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
