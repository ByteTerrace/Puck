using Puck.Hosting;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Admits a host-validated OAuth identity into an explicitly authorized World row. Every command retains that peer generation.</summary>
internal sealed class SiloControlSessionHost(WorldSiloHost silo, SiloConsoleRouting routing) : IControlSessionHost {
    /// <inheritdoc/>
    public ValueTask<ControlCapabilities> DescribeAsync(string target, ControlIdentity identity, CancellationToken cancellationToken) {
        if (!IsReady(target)) { return ValueTask.FromResult(new ControlCapabilities("")); }
        return routing.InvokeAsync(target, () => {
            if (!silo.Instances.TryGet(target, out var instance) || instance is null || silo.IsDraining ||
                !WorldAdmissionDoor.TryMatchOAuthEntry(instance.Server.Definition.Admission, identity.Issuer, identity.Subject, out var verdict) ||
                verdict.Tier != WorldDisclosureTier.Replica) { return new ControlCapabilities(""); }
            return new ControlCapabilities(routing.DescribeCommands(command => IsRemoteCommand(command.Name)));
        }, cancellationToken);
    }

    private static bool IsRemoteCommand(string name) => name is "world.wait" or "world.peers" or "world.admission" or "world.links" or
        "world.state" or "world.state.cell.set" or "world.state.cell.remove";
    /// <inheritdoc/>
    public bool IsReady(string target) => silo.Live && routing.TryGetSession(target, out _);
    /// <inheritdoc/>
    public ValueTask<IControlSession> AttachAsync(string target, ControlIdentity identity, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady(target)) { throw new InvalidOperationException("The configured row is not ready for Console ingress."); }
        return routing.InvokeAsync(target, () => {
            if (!silo.Instances.TryGet(target, out var instance) || instance is null || silo.IsDraining) { throw new InvalidOperationException("World unavailable."); }
            var server = instance.Server;
            var entries = server.Definition.Admission;
            if (!WorldAdmissionDoor.TryMatchOAuthEntry(entries, identity.Issuer, identity.Subject, out var verdict) || verdict.Tier != WorldDisclosureTier.Replica) {
                throw new UnauthorizedAccessException("The caller needs explicit OAuth admission and replica disclosure for text commands in this World.");
            }
            if (!server.TryAdmitPeerConnection(verdict, entries, out var peer, out var refusal)) { throw new UnauthorizedAccessException(refusal); }
            try {
                return routing.CreateControlSession(target, CommandPrincipal.Peer(peer.BodyIndex, peer.Generation),
                    command => Allows(command, server, peer, identity),
                    () => _ = DisconnectAsync(target, server, peer));
            } catch { server.DisconnectPeerConnection(peer); throw; }
        }, cancellationToken);
    }

    private static bool Allows(CommandMetadata command, WorldServer server, WorldPeerEventEntry peer, ControlIdentity identity) {
        // This is an explicit remote surface. New local/admin verbs never become remotely callable by registration.
        if (!IsRemoteCommand(command.Name)) { return false; }
        return server.Population.IsAdmittedPeer(peer.BodyIndex) && server.Population.PeerPrincipal(peer.BodyIndex) == peer.Identity &&
            WorldAdmissionDoor.TryMatchOAuthEntry(server.Definition.Admission, identity.Issuer, identity.Subject, out var current) &&
            current.Tier == WorldDisclosureTier.Replica;
    }

    private async Task DisconnectAsync(string target, WorldServer server, WorldPeerEventEntry peer) {
        try { await routing.InvokeAsync(target, () => { server.DisconnectPeerConnection(peer); return true; }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or OperationCanceledException) { /* Retirement discards the row and its peer table. */ }
    }
}
