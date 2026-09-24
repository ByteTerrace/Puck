using Microsoft.Extensions.Hosting;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Binds the boot row's QUIC peer endpoint while the host starts, when <c>host.listen</c>/<c>--listen</c> names one; a
/// world with no listen endpoint never opens a socket. Binding inside the host's start is what lets a bind this host
/// cannot make (<see cref="Puck.Abstractions.ListenEndpointUnavailableException"/>) end the run the way every other
/// unsupported environment does: <c>LauncherHostRun</c> reports one <c>[world.host: unsupported: …]</c> line and exits
/// with its unsupported code, and the host tears down normally. Registered ahead of every pump, so a refused bind
/// stops the start before any pump runs. The container disposes <see cref="WorldPeerHost"/>, which closes the listener.
/// </summary>
/// <param name="settings">The resolved host settings, whose <see cref="WorldHostSettings.Listen"/> names the endpoint.</param>
/// <param name="peerHost">The boot row's peer endpoint.</param>
internal sealed class WorldPeerListenerService(WorldHostSettings settings, WorldPeerHost peerHost) : IHostedService {
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) {
        if (settings.Listen is { } listen) {
            peerHost.Start(listen: listen);
        }

        return Task.CompletedTask;
    }
    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
