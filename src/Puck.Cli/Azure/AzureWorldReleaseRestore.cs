using System.Text.Json;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Captures a coherent recovery point through the source worker without stopping gameplay.</summary>
    internal static Task<WorldReleaseFixtureManifest> CaptureWorldReleasePointAsync(Guid requestId, CancellationToken token) =>
        WithManagedWorldReleaseAsync(async (context, cancellationToken) => {
            var state = await context.Groups.LoadAsync(context.Group, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("no managed group exists");
            if (state.Record.HasUnfinishedOperation || state.Record.ActiveRelease is not { } active) {
                throw new InvalidOperationException("capture requires an admitted group without unfinished work");
            }
            var source = await LoadWorldReleaseDeploymentAsync(context, active, cancellationToken).ConfigureAwait(false);
            var workers = await StableWorkersAsync(context.ResourceGroup, context.Group).ConfigureAwait(false);
            if (workers.Length != 1) { throw new InvalidOperationException("capture requires exactly one authoritative worker"); }
            var response = await WorldGuestJsonAsync(context.ResourceGroup,
                $"curl --fail --silent --show-error --max-time {source.ShutdownSeconds} -X POST http://127.0.0.1:{source.HealthPort}/release/fixture/{requestId:D}",
                Text(workers[0]!["name"])).ConfigureAwait(false);
            var point = await context.Fixtures.LoadAsync(requestId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("source did not retain its complete capture");
            if (response["identity"]?.GetValue<string>() != point.Identity || point.Release != active || point.RewindBoundary is null) {
                throw new InvalidDataException("source capture does not prove a rewindable recovery point");
            }
            return point;
        }, token);

    /// <summary>Inspects a point by default. Only explicit discard acknowledgement begins its durable restore.</summary>
    internal static Task<WorldReleaseRunResult> RestoreWorldReleaseAsync(Guid pointId, Guid? operationId,
        bool discardProgress, CancellationToken token) => WithManagedWorldReleaseAsync(async (context, cancellationToken) => {
            var restores = new WorldReleaseRestore(context.Blobs,
                AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(Text(Value(context.Outputs, "worldSiloStorageEndpoint"))), context.Owner);
            var preview = await restores.InspectAsync(context.Group, pointId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new {
                recoveryPoint = preview.Point.RequestId, identity = preview.Point.Identity, release = preview.Point.Release,
                capturedAt = preview.Point.CapturedAt, worlds = preview.Point.Worlds.Select(row => new {
                    world = row.Key, savedTick = row.Value.Tick, currentDurableTick = preview.CurrentDurableTicks[row.Key],
                }), discardsProgress = true,
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!discardProgress) { return new(false, false, "Preview only. Repeat with --discard-progress to intentionally rewind this entire group."); }
            var deployment = await LoadWorldReleaseDeploymentAsync(context, preview.Release.Identity, cancellationToken).ConfigureAwait(false);
            RequireClosedRewindDeployment(deployment);
            await RetainWorldReleaseImagesAsync([deployment.Image], cancellationToken).ConfigureAwait(false);
            var operation = operationId ?? Guid.NewGuid();
            Console.WriteLine($"Restore operation: {operation:D}");
            var begun = await restores.BeginAsync(context.Group, pointId, preview.Point.Identity, operation, true, cancellationToken).ConfigureAwait(false);
            if (!begun.Ok) { throw new InvalidOperationException(begun.Detail); }
            return await ResumeWorldReleaseCoreAsync(context, cancellationToken).ConfigureAwait(false);
        }, token);

    private static void RequireClosedRewindDeployment(AzureWorldReleaseDeployment deployment) {
        if (!deployment.Configuration.ClosedGroupRewind || deployment.Manifest.CoordinatorContract != WorldReleaseManifest.CurrentCoordinatorContract) {
            throw new InvalidDataException("retained deployment cannot enforce the rewind boundary");
        }
    }
}
