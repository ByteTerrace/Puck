using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The admission door's runtime read-back — server-safe (headless or windowed): <c>world.peers</c> echoes every body
/// the door admitted, whether it crossed as a QUIC connection (<see cref="WorldPeerHost"/>'s connection table) or as a
/// traveller an authenticated federation authority handed over. <c>world.admission</c> echoes the document half.
/// </summary>
public sealed class WorldNetworkCommandModule(IWorldConsoleAuthority authority, WorldInstanceHost? instances = null) : ICommandModule {
    private static string Describe(WorldServer server, WorldPeerHost? peerHost) {
        var arrivals = DescribeArrivals(server: server);

        if (peerHost is not { IsListening: true }) {
            return $"[world.peers: not listening — no host.listen/--listen endpoint{arrivals}]";
        }

        var connections = peerHost.Connections;
        var refusals = peerHost.FederationRefusals;
        var refusalEcho = ((refusals.Count == 0)
            ? " | federation-refusals none"
            : $" | federation-refusals {string.Join(
                separator: ",",
                values: refusals.Select(selector: static row => $"{row.Refusal}={row.Count}")
            )}"
        );

        if (connections.Count == 0) {
            return $"[world.peers: listening on {peerHost.ListenEndpoint}, 0 connections{arrivals}{refusalEcho}]";
        }

        var rows = string.Join(
            separator: " | ",
            values: connections.Select(selector: c => $"conn:{c.ConnectionId} peer:{c.PeerIndex}:{c.Generation} identity:{c.IdentityDomain}/{c.IdentitySubject} tier:{c.Tier} @ {c.RemoteEndpoint}")
        );

        return $"[world.peers: listening on {peerHost.ListenEndpoint}, {connections.Count} connections | {rows}{arrivals}{refusalEcho}]";
    }
    private static string DescribeArrivals(WorldServer server) {
        var population = server.Population;
        var rows = new List<string>();

        for (var slot = population.LocalSeatCount; (slot < population.Capacity); slot++) {
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
    // One line per authored adjacency row. Everything left of 'lane=' is tick-derived simulation state — the same
    // staleness the $link: rule channel reads and the link event family thresholds against, so a read-back and a rule
    // can never disagree. 'lane=' is the transport's own wall-clock backoff view (PersistentRequestLane.IsAvailable),
    // marked presentation-only because it must never re-enter the sim: it bounds transport lifecycle, never
    // simulation state.
    private static string DescribeLinks(WorldServer server, WorldInstanceHost? instances) {
        var definition = server.Definition;
        var rows = (definition.Adjacencies ?? []);
        var adjacencies = server.Adjacencies;
        var lines = new List<string>();

        foreach (var row in rows) {
            if (row is null) {
                continue;
            }

            var name = row.Name.Value;
            var grace = definition.AdjacencyLivenessGraceTicks(adjacency: row);
            var stale = server.Events.LinkStalenessTicks(adjacencyName: name);
            var neighbour = ((IWorldAdjacencyNeighbour?)null);
            var resolved = ((adjacencies is not null) && adjacencies.TryResolve(
                adjacencyName: name,
                neighbour: out neighbour
            ) && (neighbour is not null));
            var destination = WorldDefinitionRows.FindDestination(
                destinations: definition.Destinations,
                name: row.Destination
            );
            var reference = ((destination is null)
                ? null
                : WorldDefinitionRows.FindReference(
                    references: definition.References,
                    name: destination.Reference
                )
            );
            // The delivered identity when the seam resolves and has been stamped; the reference's own neighbour key
            // otherwise — a resolved-but-unstamped mirror is not yet naming anyone.
            var authority = ((resolved && !string.IsNullOrEmpty(value: neighbour!.Authority))
                ? neighbour.Authority
                : (reference?.NeighbourKey ?? "unresolved")
            );
            var graceEcho = (grace.IsNever
                ? "never"
                : (grace.IsZero
                    ? "off"
                    : $"{grace.Ticks}t")
            );
            var state = (grace.IsZero
                ? "unsensed"
                : ((!grace.IsNever && (stale >= grace.Ticks))
                    ? "dropped"
                    : "live")
            );
            var endpoint = string.Empty;
            var laneAvailable = false;
            // The remote table is keyed by whatever name the dial resolved under — the destinations row for a
            // transfer route, the delivered authority identity for an observation. Try both rather than reporting a
            // live lane as absent.
            var dialled = ((instances is not null) && (instances.TryDescribeRemoteAuthority(
                name: row.Destination,
                endpoint: out endpoint,
                laneAvailable: out laneAvailable
            ) || instances.TryDescribeRemoteAuthority(
                endpoint: out endpoint,
                laneAvailable: out laneAvailable,
                name: authority
            )));
            var lane = (dialled
                ? (laneAvailable
                    ? "available"
                    : "backoff")
                : "none"
            );

            lines.Add(item: $"{name} -> destination:{row.Destination} authority:{authority} endpoint:{(dialled
                ? endpoint
                : "unknown")} stale={stale}t grace={graceEcho} {state} | lane={lane} (presentation-only)");
        }

        return ((lines.Count == 0)
            ? "[world.links: this world authors no adjacencies]"
            : $"[world.links: {string.Join(
                separator: " | ",
                values: lines
            )}]"
        );
    }
    private static string DescribeProjection(WorldServer server, in WireArgs arguments) {
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
                : $"[world.projection: peer '{authority}' tier={verdict!.Tier} | {DescribeTier(
                    server: server,
                    tier: verdict.Tier
                )}]"
            );
        }

        if (string.IsNullOrWhiteSpace(value: first)) {
            return $"[world.projection: {string.Join(
                separator: " | ",
                values: Enum.GetValues<WorldDisclosureTier>().Select(selector: tier => DescribeTier(
                    server: server,
                    tier: tier
                ))
            )}]";
        }

        return ((Enum.TryParse<WorldDisclosureTier>(
            ignoreCase: true,
            result: out var requested,
            value: first
        ) && Enum.IsDefined(value: requested))
            ? $"[world.projection: {DescribeTier(
                server: server,
                tier: requested
            )}]"
            : $"[world.projection refused: '{first}' names no disclosure tier — one of {string.Join(
                separator: ", ",
                values: Enum.GetNames<WorldDisclosureTier>()
            )}]"
        );
    }
    private static string DescribeTier(WorldServer server, WorldDisclosureTier tier) {
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
            handler: context => {
                if (!authority.TryResolve(
                    context: context,
                    instance: out var instance,
                    refusal: out var refusal
                )) {
                    return CommandResult.Error(output: $"[world.peers: refused ({refusal})]");
                }

                return new CommandResult(Output: Describe(server: instance.Server, peerHost: instance.Door));
            }
        );

        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.links",
            description: "Lists every authored adjacency row's federation link: the row name, the destinations row it names, the neighbour authority identity (the delivered one when the seam currently resolves, else the reference's own neighbour key), the reference endpoint, the tick-derived staleness (stale=<n>t — simulation ticks since the last delivered neighbour refresh, the same number the '$link:<name>' rule channel reads), the compiled grace (grace=<n>t, 'off' for an unauthored livenessGraceSeconds, 'never' at simulation rate 0), and the derived live/dropped/unsensed state. The trailing 'lane=' column is the transport's own wall-clock backoff view and is PRESENTATION-ONLY — it never enters the simulation; 'none' means no remote lane has been opened for that destination (a same-process neighbour never has one). This is the read-back for the linkEstablished/linkDropped world event family. Unrelated to screen.links, which lists screen cable links.",
            valueKind: CommandValueKind.Digital,
            handler: context => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.links"
                )) {
                    return error;
                }

                return new CommandResult(Output: DescribeLinks(
                    instances: instances,
                    server: server
                ));
            }
        );

        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.projection",
            description: "Echoes what this authority would hand a peer at a named disclosure tier: 'world.projection' answers for every tier, 'world.projection <frames|presentation|replica>' for one, and 'world.projection peer <authority-namespace>' for the tier an authenticated federation authority resolves to through the admission section. Each line names the tier, the byte size of the document that tier serves, and the section inventory it carries — the redacted set is what is absent from that list. The document half is world.admission; the runtime half is world.peers' tier column.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.projection"
                )) {
                    return error;
                }

                return new CommandResult(Output: DescribeProjection(
                    arguments: in args,
                    server: server
                ));
            }
        );
    }
}
