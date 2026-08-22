using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The seat-routed document-write twins — <c>player.row.set</c> and <c>player.state.cell.set</c>: the same grammars
/// as <c>world.row.set</c> / <c>world.state.cell.set</c>, submitted through the SEAT'S CURRENT AUTHORITY ROUTE
/// (<see cref="WorldSeatAuthorityRouter"/>) instead of the local loopback, so a traveler that crossed a federation
/// seam can author the rows its row-scoped grants name — the contribution-slot fill — from the console it is
/// actually sitting at. For a seat still at the boot authority both verbs submit through the ordinary local link,
/// exactly as <c>world.row.set</c> would; for a crossed seat the mutation rides the forwarded-submission path
/// (<c>WorldFederatedServerLink.SubmitWorldMutation</c> → the federation <c>Routed</c> lane →
/// <c>WorldForwardedAuthority.TryApplySubmission</c>), where the DESTINATION re-stamps the envelope with the
/// traveler's own transfer principal from its transfer table — the wire's inner principal is never trusted — and
/// the destination's ordinary admission door (<c>TryAdmitMutation</c>, the row-scoped disjunction included) decides
/// it. The accept/reject narration therefore lands on the destination's own transcript; this console echoes only
/// where the write was routed.
/// </summary>
/// <remarks>The player index leads rather than trails: both grammars end in a greedy tail (a JSON row, a raw cell
/// token), which a trailing index would be swallowed by. Sections whose mutation composes against the addressed
/// world's own live document are refused by name (see <c>WorldRowCommandModule.TryComposeRoutedSet</c>), and the
/// routed cell write always carries the raw token for the destination's compose arm to resolve against ITS row
/// kinds — this process cannot read them.</remarks>
internal sealed class WorldRoutedRowCommandModule(PlayerRoster roster, WorldSeatAuthorityRouter seatRouter, IServerLink link, WorldClient client) : ICommandModule {
    private readonly WorldClient m_client = client;
    private readonly IServerLink m_link = link;
    private readonly PlayerRoster m_roster = roster;
    private readonly WorldSeatAuthorityRouter m_seatRouter = seatRouter;

    private CommandResult Route(CommandContext context, int display, WorldMutation mutation, string verb, string described) {
        var slot = PlayerRoster.SlotFromDisplay(number: display);

        if (
            (display < 1) ||
            (display > PlayerRoster.MaxSlots)
        ) {
            return CommandResult.Error(output: $"[{verb}: player {display} is outside 1..{PlayerRoster.MaxSlots}]");
        }

        if (!m_roster.IsJoined(slot: slot)) {
            return CommandResult.Error(output: $"[{verb}: player {display} is not joined]");
        }

        var route = m_seatRouter.Route(slot: slot);

        if (string.Equals(
            a: route.Endpoint.Identity,
            b: WorldInstanceHost.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            m_link.SubmitWorldMutation(mutation: mutation);

            return CommandResult.None;
        }

        route.Endpoint.Submissions.SubmitWorldMutation(mutation: mutation);
        // Simulation-routed like every mutation verb, so the routed acknowledgement narrates on stderr the way the
        // server's own loud lines do; the destination's transcript carries the actual verdict.
        Console.Error.WriteLine(value: $"[{verb}: routed {described} for player {display} to {route.Endpoint.Identity} — its transcript carries the verdict]");

        return CommandResult.None;
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "player.row.set",
            description: "Upserts a document row AT THE AUTHORITY THE PLAYER'S BODY CURRENTLY LIVES AT — the seat-routed twin of world.row.set, same dotted path + inline JSON grammar with the player index LEADING (the JSON tail is greedy): player.row.set <player> <path> <json>. For a seat still at the boot authority this submits the identical local mutation world.row.set would; for a seat that crossed a federation seam the mutation rides the forwarded-submission path and the DESTINATION stamps the traveler's own transfer principal before its admission door (row-scoped grants included) decides it — so a contribution-slot holder fills its slot from the console it is sitting at, and the accept/reject narration lands on the destination's transcript (this console echoes only where it routed). Paths that compose against the addressed world's own live document (inputHold, views.seatRig, views.seatControl, playerDefaults.seatLook) and the bare-name properties.names exception are refused by name.",
            handler: (context, args) => {
                if (
                    (args.Count < 3) ||
                    !int.TryParse(
                    s: args[0],
                    result: out var display
                )
                ) {
                    return CommandResult.Error(output: "[player.row.set: expected <player> <path> <json>]");
                }

                var path = args[1].ToString();
                var raw = WorldCommandArguments.RawAfter(
                    args: in args,
                    context: context,
                    tokens: 3
                );

                if (!WorldRowCommandModule.TryComposeRoutedSet(
                    error: out var error,
                    json: raw,
                    mutation: out var mutation,
                    path: path,
                    principal: context.ActingPrincipal()
                )) {
                    return CommandResult.Error(output: $"[player.row.set: {error}]");
                }

                return Route(
                    context: context,
                    described: $"'{path}'",
                    display: display,
                    mutation: mutation!,
                    verb: "player.row.set"
                );
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "player.state.cell.set",
            description: "Upserts ONE state cell AT THE AUTHORITY THE PLAYER'S BODY CURRENTLY LIVES AT — the seat-routed twin of world.state.cell.set, player index LEADING: player.state.cell.set <player> <row> <key> <value> [add]. The raw <value> token travels unresolved and the DESTINATION's compose arm resolves it against ITS OWN row's declared kind (int/fixed/bool token grammar; a text-kind row is not reachable through this routed form, since its raw-tail dispatch needs the destination's row table, which this console cannot read). Routing, principal stamping, and where the verdict lands are exactly player.row.set's.",
            handler: (context, args) => {
                if (
                    ((args.Count != 4) && (args.Count != 5)) ||
                    !int.TryParse(
                    s: args[0],
                    result: out var display
                )
                ) {
                    return CommandResult.Error(output: "[player.state.cell.set: expected <player> <row> <key> <value> [add]]");
                }

                var kind = WorldDocumentWriteKind.Set;

                if (args.Count == 5) {
                    if (!args.Is(
                        index: 4,
                        value: "add"
                    )) {
                        return CommandResult.Error(output: $"[player.state.cell.set: unknown trailing token '{args[4]}' — expected 'add']");
                    }

                    kind = WorldDocumentWriteKind.Add;
                }

                return Route(
                    context: context,
                    described: $"state cell '{args[1]}'.'{args[2]}'",
                    display: display,
                    mutation: new WorldMutation.UpsertStateCell(
                        Principal: context.ActingPrincipal(),
                        Row: args[1].ToString(),
                        Key: args[2].ToString(),
                        Value: 0L,
                        Kind: kind,
                        RawToken: args[3].ToString()
                    ),
                    verb: "player.state.cell.set"
                );
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.state.cell.toggle",
            description: "Flips ONE numeric state cell between two authored values for the ACTING seat: player.state.cell.toggle <row> <key> <a> <b> — the cell becomes <b> when it currently reads <a>, else <a>. BINDABLE with a text payload (a chord row or page entry's \"text\": \"look behind 0 3.14159\"), which is how an authored press flips a cell the camera program, a HUD, or an action reads — look behind is a seat rig whose orbit yaw binds state.look.behind and a chord that toggles it. Routes and stamps exactly as player.state.cell.set; the destination's row kind resolves the raw tokens.",
            handler: (context, args) => {
                if (args.Count != 4) {
                    return CommandResult.Error(output: "[player.state.cell.toggle: expected <row> <key> <a> <b>]");
                }

                var row = args[0].ToString();
                var key = args[1].ToString();
                var a = args[2].ToString();
                var b = args[3].ToString();
                // The flip reads the live document: the cell currently AT <a> goes to <b>; anything else goes to <a>.
                var atA = (WorldStateReader.TryRead(
                    definition: m_client.Definition,
                    key: key,
                    rawValue: out var raw,
                    row: out var stateRow,
                    rowName: row,
                    text: out _,
                    tick: m_client.Tick
                ) && (raw is { } current) && WorldStateCellWriter.TryParseNumericToken(
                    kind: stateRow.Kind,
                    reason: out _,
                    token: a,
                    value: out var rawA
                ) && (current == rawA));

                return Route(
                    context: context,
                    described: $"state cell '{row}'.'{key}' toggle",
                    display: PlayerRoster.DisplayNumber(slot: context.Slot),
                    mutation: new WorldMutation.UpsertStateCell(
                        Principal: context.ActingPrincipal(),
                        Row: row,
                        Key: key,
                        Value: 0L,
                        Kind: WorldDocumentWriteKind.Set,
                        RawToken: (atA
                            ? b
                            : a)
                    ),
                    verb: "player.state.cell.toggle"
                );
            },
            routing: CommandRouting.Simulation
        );
    }
}
