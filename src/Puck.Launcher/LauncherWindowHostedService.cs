using Microsoft.Extensions.Hosting;

namespace Puck.Launcher;

/// <summary>Runs the shared host loop with native-window presentation.</summary>
public sealed class LauncherWindowHostedService(HostLoop loop, WindowHostPresentation presentation) : BackgroundService {
    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => LauncherHostLoop.RunPump(
        name: "Puck.Launcher Window Pump",
        priority: ThreadPriority.Highest,
        pump: () => presentation.Run(loop: loop, stoppingToken: stoppingToken)
    );
}
