using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The admission door's runtime read-back — server-safe (headless or windowed): <c>world.peers</c> echoes every body
/// the door admitted, whether it crossed as a TCP connection (<see cref="WorldTcpHost"/>'s connection table) or as a
/// traveller an authenticated federation authority handed over. <c>world.admission</c> echoes the document half.
/// </summary>
internal sealed class WorldNetworkCommandModule(WorldTcpHost tcpHost, WorldServer server) : ICommandModule {
    private string Describe() {
        var arrivals = DescribeArrivals();

        if (!tcpHost.IsListening) {
            return $"[world.peers: not listening — no host.listen/--listen endpoint{arrivals}]";
        }

        var connections = tcpHost.Connections;
        var refusals = tcpHost.FederationRefusals;
        var refusalEcho = ((refusals.Count == 0)
            ? " | federation-refusals none"
            : $" | federation-refusals {string.Join(
                separator: ",",
                values: refusals.Select(selector: static row => $"{row.Refusal}={row.Count}")
            )}"
        );

        if (connections.Count == 0) {
            return $"[world.peers: listening on {tcpHost.ListenEndpoint}, 0 connections{arrivals}{refusalEcho}]";
        }

        var rows = string.Join(
            separator: " | ",
            values: connections.Select(selector: c => $"conn:{c.ConnectionId} peer:{c.PeerIndex}:{c.Generation} identity:{c.IdentityDomain}/{c.IdentitySubject} tier:{c.Tier} @ {c.RemoteEndpoint}")
        );

        return $"[world.peers: listening on {tcpHost.ListenEndpoint}, {connections.Count} connections | {rows}{arrivals}{refusalEcho}]";
    }
    private string DescribeArrivals() {
        var population = server.Population;
        var rows = new List<string>();

        for (var slot = WorldPopulation.LocalSeatCount; (slot < population.Capacity); slot++) {
            if (
                !population.IsActive(index: slot) ||
                !population.PeerAuthorityTransferred(bodyIndex: slot) ||
                !population.IsAdmittedPeer(bodyIndex: slot)
            ) {
                continue;
            }

            rows.Add(item: $"body:{slot} {population.PeerPrincipal(index: slot).Describe()} authority:{population.PeerIdentity(bodyIndex: slot).Domain}");
        }

        return ((rows.Count == 0)
            ? string.Empty
            : $" | arrivals: {string.Join(
                separator: " | ",
                values: rows
            )}"
        );
    }
    private string DescribeProjection(in WireArgs arguments) {
        var first = ((arguments.Count > 0)
            ? arguments[0].ToString()
            : string.Empty
        );

        if (string.Equals(
            a: first,
            b: "peer",
            comparisonType: StringComparison.Ordinal
        )) {
            var authority = ((arguments.Count > 1)
                ? arguments[1].ToString()
                : string.Empty
            );

            if (string.IsNullOrWhiteSpace(value: authority)) {
                return "[world.projection refused: 'peer' needs an authority namespace]";
            }

            var refusal = WorldAdmissionDoor.TryAdmitArrival(
                entries: server.Definition.Admission,
                sourceAuthority: authority,
                verdict: out var verdict
            );

            return ((refusal is { } named)
                ? $"[world.projection: peer '{authority}' {named} — no admission row names it; the wire default {WorldDisclosureTier.Presentation} applies]"
                : $"[world.projection: peer '{authority}' tier={verdict!.Tier} | {DescribeTier(tier: verdict.Tier)}]"
            );
        }

        if (string.IsNullOrWhiteSpace(value: first)) {
            return $"[world.projection: {string.Join(
                separator: " | ",
                values: Enum.GetValues<WorldDisclosureTier>().Select(selector: DescribeTier)
            )}]";
        }

        return ((Enum.TryParse<WorldDisclosureTier>(
            ignoreCase: true,
            result: out var requested,
            value: first
        ) && Enum.IsDefined(value: requested))
            ? $"[world.projection: {DescribeTier(tier: requested)}]"
            : $"[world.projection refused: '{first}' names no disclosure tier — one of {string.Join(
                separator: ", ",
                values: Enum.GetNames<WorldDisclosureTier>()
            )}]"
        );
    }
    private string DescribeTier(WorldDisclosureTier tier) {
        var definition = server.Definition;

        if (tier == WorldDisclosureTier.Frames) {
            return "frames: 0 bytes, no document";
        }

        if (tier == WorldDisclosureTier.Replica) {
            return $"replica: {WorldDefinitionSerialization.Serialize(definition: definition).Length} bytes, the whole {WorldDefinition.SchemaVersion} document";
        }

        var projection = WorldProjection.Compose(
            definition: definition,
            tier: tier,
            authority: server.AuthorityIdentity,
            revision: server.Population.Revision
        )!;
        var bytes = WorldProjection.Serialize(projection: projection);

        return $"presentation: {bytes.Length} bytes, {WorldProjectionDocument.SchemaVersion} carrying {string.Join(
            separator: ",",
            values: ProjectionSections(projection: projection)
        )}";
    }
    // The section inventory a reader compares against the world document's own to see what the tier withheld.
    private static IEnumerable<string> ProjectionSections(WorldProjectionDocument projection) {
        using var document = System.Text.Json.JsonDocument.Parse(utf8Json: WorldProjection.Serialize(projection: projection));

        foreach (var member in document.RootElement.EnumerateObject()) {
            yield return member.Name;
        }
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.peers",
            description: "Lists every body the admission door admitted: whether the host is listening (and on what endpoint), one line per admitted remote connection — connection id, its peer principal (peer:<index>:<generation>), its verified admission identity (domain/subject), and the remote endpoint — then one line per arrived traveller, naming the body, its peer principal, and the authenticated source authority its verdict was decided against, then every federation refusal this door has written, counted by its stable name. Identity fields are only ever what the door verified, never what a payload asserted. Not listening (no host.listen/--listen) prints that plainly rather than an empty table.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: Describe())
        );

        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.projection",
            description: "Echoes what this authority would hand a peer at a named disclosure tier: 'world.projection' answers for every tier, 'world.projection <frames|presentation|replica>' for one, and 'world.projection peer <authority-namespace>' for the tier an authenticated federation authority resolves to through the admission section. Each line names the tier, the byte size of the document that tier serves, and the section inventory it carries — the redacted set is what is absent from that list. The document half is world.admission; the runtime half is world.peers' tier column.",
            handler: (_, args) => new CommandResult(Output: DescribeProjection(arguments: in args))
        );
    }
}
