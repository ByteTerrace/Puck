using Microsoft.Extensions.Hosting;

namespace Puck.Launcher;

/// <summary>Runs the shared host loop without presentation.</summary>
public sealed class HeadlessTickHostedService(HostLoop loop) : BackgroundService {
    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => LauncherHostLoop.RunPump(
        name: "Puck.Launcher Headless Tick Pump",
        pump: () => loop.Run(stoppingToken: stoppingToken)
    );
}
