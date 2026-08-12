using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The admission door's runtime read-back — server-safe (headless or windowed): <c>world.peers</c> echoes every body
/// the door admitted, whether it crossed as a TCP connection (<see cref="WorldTcpHost"/>'s connection table) or as a
/// traveller an authenticated federation authority handed over. <c>world.admission</c> echoes the document half.
/// </summary>
internal sealed class WorldNetworkCommandModule(WorldTcpHost tcpHost, WorldServer server) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.peers",
            description: "Lists every body the admission door admitted: whether the host is listening (and on what endpoint), one line per admitted remote connection — connection id, its peer principal (peer:<index>:<generation>), its verified admission identity (domain/subject), and the remote endpoint — then one line per arrived traveller, naming the body, its peer principal, and the authenticated source authority its verdict was decided against. Identity fields are only ever what the door verified, never what a payload asserted.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: Describe())
        );
    }

    private string Describe() {
        var arrivals = DescribeArrivals();

        if (!tcpHost.IsListening) {
            return $"[world.peers: not listening — no host.listen/--listen endpoint{arrivals}]";
        }

        var connections = tcpHost.Connections;

        if (connections.Count == 0) {
            return $"[world.peers: listening on {tcpHost.ListenEndpoint}, 0 connections{arrivals}]";
        }

        var rows = string.Join(separator: " | ", values: connections.Select(selector: c => $"conn:{c.ConnectionId} peer:{c.PeerIndex}:{c.Generation} identity:{c.IdentityDomain}/{c.IdentitySubject} @ {c.RemoteEndpoint}"));

        return $"[world.peers: listening on {tcpHost.ListenEndpoint}, {connections.Count} connections | {rows}{arrivals}]";
    }

    private string DescribeArrivals() {
        var population = server.Population;
        var rows = new List<string>();

        for (var slot = WorldPopulation.LocalSeatCount; (slot < population.Capacity); slot++) {
            if (!population.IsActive(index: slot) || !population.PeerAuthorityTransferred(bodyIndex: slot) || !population.IsAdmittedPeer(bodyIndex: slot)) {
                continue;
            }

            rows.Add(item: $"body:{slot} {population.PeerPrincipal(index: slot).Describe()} authority:{population.PeerIdentity(bodyIndex: slot).Domain}");
        }

        return ((rows.Count == 0) ? string.Empty : $" | arrivals: {string.Join(separator: " | ", values: rows)}");
    }
}
