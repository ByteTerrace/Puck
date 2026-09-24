using System.Text.Json;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Captures a coherent recovery point through the source worker without stopping gameplay.</summary>
    internal static Task<WorldReleaseFixtureManifest> CaptureWorldReleasePointAsync(Guid requestId, TimeProvider clock, CancellationToken token) =>
        WithManagedWorldReleaseAsync(
            async (context, cancellationToken) => {
                var state = (await context.Groups.LoadAsync(
                    context.Group,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false)
                    ?? throw new InvalidOperationException(message: "no managed group exists"));

                if (
                    state.Record.HasUnfinishedOperation ||
                    (state.Record.ActiveRelease is not { } active)
                ) {
                    throw new InvalidOperationException(message: "capture requires an admitted group without unfinished work");
                }
                var source = await LoadWorldReleaseDeploymentAsync(
                    context: context,
                    identity: active,
                    token: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
                var workers = await StableWorkersAsync(
                    clock: context.Clock,
                    group: context.ResourceGroup,
                    scaleSet: context.Group
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (workers.Length != 1) { throw new InvalidOperationException(message: "capture requires exactly one authoritative worker"); }
                var response = await WorldGuestJsonAsync(
                    group: context.ResourceGroup,
                    script: $"curl --fail --silent --show-error --max-time {source.ShutdownSeconds} -X POST http://127.0.0.1:{source.HealthPort}/release/fixture/{requestId:D}",
                    worker: Text(value: workers[0]!["name"])
                ).ConfigureAwait(continueOnCapturedContext: false);
                var point = (await context.Fixtures.LoadAsync(
                    cancellationToken: cancellationToken,
                    requestId: requestId
                ).ConfigureAwait(continueOnCapturedContext: false)
                    ?? throw new InvalidDataException(message: "source did not retain its complete capture"));

                if (
                    (response["identity"]?.GetValue<string>() != point.Identity) ||
                    (point.Release != active) ||
                    (point.RewindBoundary is null)
                ) {
                    throw new InvalidDataException(message: "source capture does not prove a rewindable recovery point");
                }
                return point;
            },
            clock,
            token
        );
    /// <summary>Inspects a point by default. Only explicit discard acknowledgement begins its durable restore.</summary>
    internal static Task<WorldReleaseRunResult> RestoreWorldReleaseAsync(Guid pointId, Guid? operationId,
        bool discardProgress, TimeProvider clock, CancellationToken token) => WithManagedWorldReleaseAsync(
            async (context, cancellationToken) => {
                var restores = new WorldReleaseRestore(
                    context.Blobs,
                    AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: Text(value: Value(
                        context.Outputs,
                        "worldSiloStorageEndpoint"
                    ))),
                    context.Owner
                );
                var preview = await restores.InspectAsync(
                    group: context.Group,
                    pointId: pointId,
                    token: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: JsonSerializer.Serialize(
                    new {
                        recoveryPoint = preview.Point.RequestId,
                        identity = preview.Point.Identity,
                        release = preview.Point.Release,
                        capturedAt = preview.Point.CapturedAt,
                        worlds = preview.Point.Worlds.Select(selector: row => new {
                            world = row.Key,
                            savedTick = row.Value.Tick,
                            currentDurableTick = preview.CurrentDurableTicks[row.Key],
                        }),
                        discardsProgress = true,
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                ));
                if (!discardProgress) {
                    return new(
                        false,
                        false,
                        "Preview only. Repeat with --discard-progress to intentionally rewind this entire group."
                    );
                }
                var deployment = await LoadWorldReleaseDeploymentAsync(
                    context: context,
                    identity: preview.Release.Identity,
                    token: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                RequireClosedRewindDeployment(deployment: deployment);
                await RetainWorldReleaseImagesAsync(
                    [deployment.Image],
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
                var operation = (operationId ?? Guid.NewGuid());

                Console.WriteLine(value: $"Restore operation: {operation:D}");
                var begun = await restores.BeginAsync(
                    context.Group,
                    pointId,
                    preview.Point.Identity,
                    operation,
                    true,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!begun.Ok) { throw new InvalidOperationException(message: begun.Detail); }
                return await ResumeWorldReleaseCoreAsync(
                    context: context,
                    token: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
            },
            clock,
            token
        );

    private static void RequireClosedRewindDeployment(AzureWorldReleaseDeployment deployment) {
        if (!deployment.Configuration.ClosedGroupRewind) {
            throw new InvalidDataException(message: "retained deployment cannot enforce the rewind boundary");
        }
    }
}
