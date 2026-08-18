using Microsoft.Extensions.Hosting;

namespace Puck.World.Silo;

/// <summary>Activates every <c>pinned</c> row at silo start. Runs from <see cref="ExecuteAsync"/>, never
/// <c>StartAsync</c> — activation completes only once the tick thread drains the activation mailbox, and that
/// thread is spawned by the headless tick host's own <c>StartAsync</c>; awaiting activation from this service's own
/// <c>StartAsync</c> would deadlock the host waiting on a pump that has not started yet.</summary>
internal sealed class WorldSiloActivations(WorldSiloDefinition definition, IGrainFactory grainFactory) : BackgroundService {
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        foreach (var world in definition.Worlds) {
            if (!world.Pinned) {
                continue;
            }

            var grain = grainFactory.GetGrain<IWorldGrain>(
                primaryKey: world.Owner,
                keyExtension: world.World.Value
            );
            var activated = await grain.ActivateAsync();

            if (!activated) {
                Console.Error.WriteLine(value: $"[silo.activate: pinned row 'owner/{world.Owner:D}/{world.World}' did not activate]");
            }
        }
    }
}
