using Microsoft.Extensions.Hosting;
using Puck.Input;

namespace Puck.Launcher;

/// <summary>
/// Owns the <see cref="GamepadManager"/> lifecycle: it enumerates and starts controllers when the host starts
/// and tears them down on shutdown. The manager's input is consumed by a <see cref="GamepadInputSource"/>
/// already registered with the command registry, so this service only governs device lifetime.
/// </summary>
public sealed class GamepadHostedService : IHostedService {
    private readonly GamepadManager m_manager;

    /// <summary>Initializes a new instance of the <see cref="GamepadHostedService"/> class.</summary>
    /// <param name="manager">The gamepad manager to start and dispose.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <see langword="null"/>.</exception>
    public GamepadHostedService(GamepadManager manager) {
        ArgumentNullException.ThrowIfNull(manager);

        m_manager = manager;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) {
        m_manager.Start();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) {
        m_manager.Dispose();

        return Task.CompletedTask;
    }
}
