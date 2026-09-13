using Puck.Hosting;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Admits a host-validated OAuth identity into an explicitly authorized World row. Every command retains that peer generation.</summary>
internal sealed class SiloControlSessionHost(WorldSiloHost silo, SiloConsoleRouting routing) : IControlSessionHost {
    private static bool Allows(CommandMetadata command, WorldServer server, WorldPeerEventEntry peer, ControlIdentity identity) {
        // This is an explicit remote surface. New local/admin verbs never become remotely callable by registration.
        if (!IsRemoteCommand(name: command.Name)) { return false; }
        return (
            server.Population.IsAdmittedPeer(bodyIndex: peer.BodyIndex) &&
            (server.Population.PeerPrincipal(index: peer.BodyIndex) == peer.Identity) &&
            WorldAdmissionDoor.TryMatchOAuthEntry(
            entries: server.Definition.Admission,
            issuer: identity.Issuer,
            subject: identity.Subject,
            verdict: out var current
        ) &&
            (current.Tier == WorldDisclosureTier.Replica)
        );
    }
    private async Task DisconnectAsync(string target, WorldServer server, WorldPeerEventEntry peer) {
        try { await routing.InvokeAsync(
            target,
            () => { server.DisconnectPeerConnection(peer: peer); return true; },
            CancellationToken.None
        ).ConfigureAwait(continueOnCapturedContext: false); } catch (Exception error) when ((error is ObjectDisposedException or InvalidOperationException or OperationCanceledException)) { /* Retirement discards the row and its peer table. */ }
    }
    private static bool IsRemoteCommand(string name) => (name is "world.wait" or "world.peers" or "world.admission" or "world.links" or
        "world.state" or "world.state.cell.set" or "world.state.cell.remove");

    /// <inheritdoc/>
    public ValueTask<IControlSession> AttachAsync(string target, ControlIdentity identity, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady(target: target)) { throw new InvalidOperationException(message: "The configured row is not ready for Console ingress."); }
        return routing.InvokeAsync(
            target,
            () => {
            if (
                !silo.Instances.TryGet(
                instance: out var instance,
                name: target
            ) ||
                (instance is null) ||
                silo.IsDraining
            ) { throw new InvalidOperationException(message: "World unavailable."); }
            var server = instance.Server;
            var entries = server.Definition.Admission;

            if (
                !WorldAdmissionDoor.TryMatchOAuthEntry(
                entries: entries,
                issuer: identity.Issuer,
                subject: identity.Subject,
                verdict: out var verdict
            ) ||
                (verdict.Tier != WorldDisclosureTier.Replica)
            ) {
                throw new UnauthorizedAccessException(message: "The caller needs explicit OAuth admission and replica disclosure for text commands in this World.");
            }
            if (!server.TryAdmitPeerConnection(
                admitted: out var peer,
                expectedAdmissionEntries: entries,
                refusal: out var refusal,
                verdict: verdict
            )) { throw new UnauthorizedAccessException(message: refusal); }
            try {
                return routing.CreateControlSession(
                    target,
                    CommandPrincipal.Peer(
                        peer.BodyIndex,
                        peer.Generation
                    ),
                    command => Allows(
                        command: command,
                        identity: identity,
                        peer: peer,
                        server: server
                    ),
                    () => _ = DisconnectAsync(
                        peer: peer,
                        server: server,
                        target: target
                    )
                );
            } catch { server.DisconnectPeerConnection(peer: peer); throw; }
        },
            cancellationToken
        );
    }
    /// <inheritdoc/>
    public ValueTask<ControlCapabilities> DescribeAsync(string target, ControlIdentity identity, CancellationToken cancellationToken) {
        if (!IsReady(target: target)) { return ValueTask.FromResult(result: new ControlCapabilities("")); }
        return routing.InvokeAsync(
            target,
            () => {
            if (
                !silo.Instances.TryGet(
                instance: out var instance,
                name: target
            ) ||
                (instance is null) ||
                silo.IsDraining ||
                !WorldAdmissionDoor.TryMatchOAuthEntry(
                entries: instance.Server.Definition.Admission,
                issuer: identity.Issuer,
                subject: identity.Subject,
                verdict: out var verdict
            ) ||
                (verdict.Tier != WorldDisclosureTier.Replica)
            ) { return new ControlCapabilities(""); }
            return new ControlCapabilities(routing.DescribeCommands(include: command => IsRemoteCommand(name: command.Name)));
        },
            cancellationToken
        );
    }
    /// <inheritdoc/>
    public bool IsReady(string target) => (silo.Live && routing.TryGetSession(
        session: out _,
        worldId: target
    ));
}
