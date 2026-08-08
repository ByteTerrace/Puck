using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The TCP socket's read-back verb surface — SERVER-SAFE (headless or windowed): <c>world.peers</c> echoes the
/// connection table <see cref="WorldTcpHost"/> owns. The design's read-back rule (no decision surface without an
/// echoing verb) applies to admission exactly like every other grant/mutation decision.
/// </summary>
internal sealed class WorldNetworkCommandModule(WorldTcpHost tcpHost) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.peers",
            description: "Lists the TCP socket's connection table: whether the host is listening (and on what endpoint), then one line per admitted remote connection — connection id, its peer principal (peer:<index>:<generation>), and the remote endpoint. Not listening (no host.listen/--listen) prints that plainly rather than an empty table.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: DescribeConnections())
        );
    }

    private string DescribeConnections() {
        if (!tcpHost.IsListening) {
            return "[world.peers: not listening — no host.listen/--listen endpoint]";
        }

        var connections = tcpHost.Connections;

        if (connections.Count == 0) {
            return $"[world.peers: listening on {tcpHost.ListenEndpoint}, 0 connections]";
        }

        var rows = string.Join(separator: " | ", values: connections.Select(selector: c => $"conn:{c.ConnectionId} peer:{c.PeerIndex}:{c.Generation} @ {c.RemoteEndpoint}"));

        return $"[world.peers: listening on {tcpHost.ListenEndpoint}, {connections.Count} connections | {rows}]";
    }
}
