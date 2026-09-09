using Microsoft.Extensions.Hosting;

namespace Puck.World.Silo;

/// <summary>Activates every <c>pinned</c> row at silo start. Runs from <see cref="ExecuteAsync"/>, never
/// <c>StartAsync</c> — activation completes only once the tick thread drains the activation mailbox, and that
/// thread is spawned by the headless tick host's own <c>StartAsync</c>; awaiting activation from this service's own
/// <c>StartAsync</c> would deadlock the host waiting on a pump that has not started yet.</summary>
internal sealed class WorldSiloActivations(WorldSiloDefinition definition, IGrainFactory grainFactory, IHostApplicationLifetime lifetime) : BackgroundService {
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            // The tick host and Orleans membership must both be ready before requesting a grain placement.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken);
            foreach (var world in definition.Worlds) {
                if (!world.Pinned) {
                    continue;
                }

                var grain = grainFactory.GetGrain<IWorldGrain>(
                    primaryKey: world.Owner,
                    keyExtension: world.World.Value
                );
                var activated = await grain.ActivateAsync();

                // Establish a durable baseline before reporting startup success: journals require a checkpoint.
                if (!activated || !await grain.CheckpointNowAsync()) {
                    Environment.ExitCode = 1;
                    throw new InvalidOperationException($"Pinned row 'owner/{world.Owner:D}/{world.World}' did not activate and checkpoint.");
                }
            }
        } catch (Exception) when (!stoppingToken.IsCancellationRequested) {
            Environment.ExitCode = 1;
            throw;
        }
    }
}
