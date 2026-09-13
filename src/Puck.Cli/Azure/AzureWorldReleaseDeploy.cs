using System.Text.Json;
using Puck.Cli.Automation;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Retains and qualifies an exact package before entering the durable maintenance transaction.</summary>
    internal static async Task<WorldReleaseRunResult> DeployWorldReleaseAsync(string packageDirectory, string fixtureDirectory,
        Guid? operationId, CancellationToken cancellationToken, string? resourceGroup = null) {
        if (operationId == Guid.Empty) { throw new ArgumentException("release operation ID must not be empty", nameof(operationId)); }
        packageDirectory = Path.GetFullPath(packageDirectory);
        fixtureDirectory = Path.GetFullPath(fixtureDirectory);
        var manifest = JsonSerializer.Deserialize<WorldReleaseManifest>(ConfinedFile.ReadAllBytes(Path.Combine(packageDirectory, "release.json"), 1024 * 1024))
            ?? throw new InvalidDataException("release package contains no manifest");
        if (!WorldReleaseManifest.TryVerify(manifest, packageDirectory, out var reason)) { throw new InvalidDataException(reason); }
        return await WithManagedWorldReleaseAsync(async (context, token) => {
            var current = await context.Groups.LoadAsync(context.Group, token).ConfigureAwait(false);
            if (current is { } pending && pending.Record.PendingOperationId is { } existingOperation) {
                if (pending.Record.PendingTargetRelease != manifest.Identity || (operationId is { } requested && requested != existingOperation)) {
                    throw new InvalidOperationException($"operation {existingOperation:D} is already pending; run 'puck world release resume'");
                }
                return await ResumeWorldReleaseCoreAsync(context, token).ConfigureAwait(false);
            }
            if (current?.Record.ActiveRelease == manifest.Identity) {
                var active = await LoadWorldReleaseDeploymentAsync(context, manifest.Identity, token).ConfigureAwait(false);
                await TestWorldReleaseAsync(context.ResourceGroup, active.Image, context.Group).ConfigureAwait(false);
                return new WorldReleaseRunResult(true, false, "the exact release is already active and healthy", current);
            }
            if (current?.Record.RollbackEligible == true) {
                throw new InvalidOperationException("the previous release is still retained for rollback; finalize that window before deploying another release");
            }
            if (current is null) {
                if ((await WorkersAsync(context.ResourceGroup, context.Group).ConfigureAwait(false)).Length != 0) {
                    throw new InvalidOperationException("an existing unmanaged worker cannot be adopted as an empty bootstrap");
                }
                var created = await context.Groups.CreateAsync(context.Group, null, token).ConfigureAwait(false);
                if (!created.Ok) { throw new InvalidOperationException(created.Detail); }
                current = created.Snapshot;
            }
            var state = current!.Value;
            AzureWorldReleaseDeployment? source = null;
            if (state.Record.ActiveRelease is { } sourceIdentity) {
                source = await LoadWorldReleaseDeploymentAsync(context, sourceIdentity, token).ConfigureAwait(false);
                if (!WorldReleaseTransitionPolicy.TryPrepare(source.Manifest, manifest, out _, out reason)) { throw new InvalidDataException(reason); }
            }
            if (!Directory.Exists(fixtureDirectory)) { throw new DirectoryNotFoundException("release qualification requires a coherent offline fixture directory"); }
            // Reuse already retained inputs on retry. Regenerating SSH material or reading today's template would
            // otherwise turn a lost preflight response into a conflicting configuration for the same release.
            await context.Archive.SaveAsync(manifest, packageDirectory, token).ConfigureAwait(false);
            var retained = await context.Deployments.LoadAsync(manifest, context.Group, token).ConfigureAwait(false);
            if (retained is null) {
                retained = await PrepareWorldReleaseDeploymentAsync(manifest, context.ResourceGroup).ConfigureAwait(false);
                await context.Deployments.SaveAsync(manifest, retained, token).ConfigureAwait(false);
            }
            var candidate = await LoadWorldReleaseDeploymentAsync(context, manifest.Identity, token).ConfigureAwait(false);
            await RetainWorldReleaseImagesAsync(source is null ? [candidate.Image] : [source.Image, candidate.Image], token).ConfigureAwait(false);
            var runner = new WorldReleaseQualificationRunner(fixtureDirectory, Path.GetFullPath("artifacts/world-release-qualification"),
                source?.Image ?? candidate.Image, candidate.Image);
            var coordinator = new WorldReleaseCoordinator(context.Groups);
            var operation = operationId ?? Guid.NewGuid();
            Console.WriteLine($"Release operation: {operation:D}");
            var begun = source is null
                ? await coordinator.BeginBootstrapAsync(state, manifest, runner, operation, token).ConfigureAwait(false)
                : await coordinator.BeginDeploymentAsync(state, source.Manifest, manifest, runner, operation, token).ConfigureAwait(false);
            if (!begun.Ok) { throw new InvalidOperationException(begun.Detail); }
            return await ResumeWorldReleaseCoreAsync(context, token).ConfigureAwait(false);
        }, cancellationToken, resourceGroup).ConfigureAwait(false);
    }

    private static async Task DeployWorldAsync(string? group, string package, string fixture, Guid? operation, CancellationToken token) {
        var result = await DeployWorldReleaseAsync(package, fixture, operation, token, group).ConfigureAwait(false);
        Console.WriteLine(result.Detail);
        if (!result.Completed || result.SourceRecovered) { throw new InvalidOperationException("release did not complete; inspect 'puck world release status' and resume its durable operation"); }
    }
}
