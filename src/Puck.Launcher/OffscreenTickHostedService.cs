using Microsoft.Extensions.Hosting;

namespace Puck.Launcher;

/// <summary>Runs the shared host loop with offscreen presentation.</summary>
public sealed class OffscreenTickHostedService(HostLoop loop, OffscreenHostPresentation presentation) : BackgroundService {
    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => LauncherHostLoop.RunPump(
        name: "Puck.Launcher Offscreen Tick Pump",
        pump: () => presentation.Run(loop: loop, stoppingToken: stoppingToken)
    );
}
