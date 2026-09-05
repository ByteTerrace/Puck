using System.Globalization;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldStateCommandModule {
    private IEnumerable<CommandDefinition> DiscreteCommands() {
        yield return CommandDefinition.Verb(
            name: "world.state.observe", bindability: CommandBindability.Unbindable,
            description: "Reads the literal state observations admitted for the authenticated caller. Hidden cells and identities are omitted.",
            valueKind: CommandValueKind.Digital, routing: CommandRouting.Immediate,
            handler: context => {
                var result = default(CommandResult);
                void Complete(QueryAnswer answer) => result = new(Output: answer.Text) { IsError = answer.Refused };
                if (link is IPrincipalServerLink stamped) {
                    stamped.Query(new WorldQuery.StateObservations(), context.ActingPrincipal(), Complete);
                } else {
                    link.Query(new WorldQuery.StateObservations(), Complete);
                }
                return result;
            });
        yield return CommandDefinition.WithWireArgs(
            name: "world.state.transform", bindability: CommandBindability.Unbindable,
            description: "Applies one atomic state transform: world.state.transform <transform-json>. Checks edit authority over every touched row.",
            routing: CommandRouting.Simulation,
            handler: (context, args) => SubmitTransform(context, args, guarded: false));
        yield return CommandDefinition.WithWireArgs(
            name: "world.state.act", bindability: CommandBindability.Unbindable,
            description: "Submits a phase-guarded state operation: world.state.act <phase-row> <sequence> <transform-json>. Refuses stale, ineligible, ready or expired actions.",
            routing: CommandRouting.Simulation,
            handler: (context, args) => SubmitTransform(context, args, guarded: true));
        yield return CommandDefinition.WithWireArgs(
            name: "world.topologies", bindability: CommandBindability.Unbindable,
            description: "Prints named physical and discrete topology declarations and compiled addressing costs.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(args, "world.topologies") is { } refusal) {
                    return refusal;
                }
                if (!authority.TryResolveServer(context, "world.topologies", out var server, out var error)) {
                    return error;
                }
                var lines = new List<string>();
                foreach (var topology in server.Definition.StateRaw?.Lattices ?? []) {
                    var compiled = WorldTopologyCompilation.Find(server.Definition.StateRaw, topology.Name);
                    var directionCount = compiled?.DirectionCount ?? 0;
                    var names = (compiled is null)
                        ? "none"
                        : string.Join(",", Enumerable.Range(0, directionCount).Select(compiled.DirectionName));
                    var normalized = WorldTopologyCompilation.Normalize(topology);
                    lines.Add($"[world.topology '{topology.Name}' kind={topology.Kind} cells={compiled?.CellCount ?? normalized.Width * normalized.Depth * normalized.Layers} directions={directionCount} names={names} wrap={normalized.Wrap}]");
                }
                return new CommandResult(Output: string.Join(Environment.NewLine, lines));
            });
        yield return CommandDefinition.WithWireArgs(
            name: "world.patterns", bindability: CommandBindability.Unbindable,
            description: "Echoes every compiled pattern language: kind, refined letters, machine states against the row's budget, and the attribute row a zone source reads.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(args, "world.patterns") is { } refusal) {
                    return refusal;
                }
                if (!authority.TryResolveServer(context, "world.patterns", out var server, out var error)) {
                    return error;
                }
                return new CommandResult(Output: server.DescribePatterns());
            });
        yield return CommandDefinition.WithWireArgs(
            name: "world.tables", bindability: CommandBindability.Unbindable,
            description: "Echoes every static lookup table the document references: name, value kind, and entry count.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(args, "world.tables") is { } refusal) {
                    return refusal;
                }
                if (!authority.TryResolveServer(context, "world.tables", out var server, out var error)) {
                    return error;
                }
                return new CommandResult(Output: server.DescribeTables());
            });
        yield return CommandDefinition.WithWireArgs(
            name: "world.topology", bindability: CommandBindability.Unbindable,
            description: "Lists a discrete topology's point-group elements, and with a cell key, that cell's image under each: world.topology <topology> [<cell>].",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (args.Count is < 1 or > 2) {
                    return CommandResult.Usage("world.topology", "<topology> [<cell>]");
                }
                if (!authority.TryResolveServer(context, "world.topology", out var server, out var error)) {
                    return error;
                }
                return new CommandResult(Output: server.DescribeSymmetry(topologyName: args[0].ToString(), cellKey: (args.Count == 2) ? args[1].ToString() : null));
            });
        yield return CommandDefinition.WithWireArgs(
            name: "world.observe", bindability: CommandBindability.Unbindable,
            description: "Composes the literal state observations an EXPLICITLY NAMED principal would see — the read-back side of a hidden-hand table, for inspecting another seat's disclosure without submitting as it: world.observe <principal>. Same token grammar as world.grant (WorldPrincipal.TryParse). Unlike world.state.observe (which reads the CALLER's own stamped identity), this composes for the named principal directly through WorldStateDisclosure.Compose, the same trusted-authority read world.why/world.grants already use — a console/authority tool, not a wire capability check.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (args.Count != 1) {
                    return CommandResult.Usage("world.observe", "<principal>");
                }
                if (!authority.TryResolveServer(context, "world.observe", out var server, out var error)) {
                    return error;
                }
                if (!WorldGrantCommandModule.TryParsePrincipal(args[0].ToString(), out var principal)) {
                    return CommandResult.Error($"[world.observe: unknown principal '{args[0]}' — {WorldPrincipal.TokenGrammar}]");
                }
                var rows = (WorldStateDisclosure.Compose(server.Definition, principal) ?? []).ToArray();
                return new CommandResult(Output: System.Text.Json.JsonSerializer.Serialize(rows, WorldJsonContext.Default.WorldObservedRowArray));
            });
        yield return CommandDefinition.WithWireArgs(
            name: "world.match", bindability: CommandBindability.Unbindable,
            description: "Walks one word through a pattern and narrates it: world.match <pattern> <row> [<attribute>] for a keyed row or zone, world.match <pattern> <board-row> <origin-cell> <direction|any> for a board.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (args.Count is < 2 or > 4) {
                    return CommandResult.Usage("world.match", "<pattern> <row> [<attribute>] | <pattern> <board-row> <origin-cell> <direction|any>");
                }
                if (!authority.TryResolveServer(context, "world.match", out var server, out var error)) {
                    return error;
                }
                var pattern = args[0].ToString();
                var row = args[1].ToString();
                return new CommandResult(Output: (args.Count == 4)
                    ? server.DescribeMatch(patternName: pattern, rowName: row, attribute: null, key: args[2].ToString(), direction: args[3].ToString())
                    : server.DescribeMatch(patternName: pattern, rowName: row, attribute: (args.Count == 3) ? args[2].ToString() : null, key: null, direction: null));
            });
    }

    private CommandResult SubmitTransform(CommandContext context, WireArgs args, bool guarded) {
        var verb = guarded ? "world.state.act" : "world.state.transform";
        var prefix = guarded ? 3 : 1;
        if (args.Count < prefix) {
            return CommandResult.Usage(verb, guarded ? "<phase-row> <sequence> <transform-json>" : "<transform-json>");
        }
        WorldPhaseGuard? guard = null;
        if (guarded) {
            if (!long.TryParse(args[1].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)) {
                return CommandResult.Error("phase sequence must be a nonnegative integer");
            }
            guard = new(args[0].ToString(), sequence);
        }
        try {
            var json = WorldCommandArguments.RawAfter(args: in args, context: context, tokens: prefix);
            var operation = JsonSerializer.Deserialize(json, WorldJsonContext.Default.WorldStateTransform);
            if (operation is null) {
                return CommandResult.Error("state transform must be an object");
            }
            return link.Submit(new WorldMutation.TransformState(context.ActingPrincipal(), operation, guard), echoes, verb);
        } catch (JsonException exception) {
            return CommandResult.Error($"invalid state transform: {exception.Message}");
        }
    }

    private static string DescribeDiscrete(WorldServer server, WorldStateRow row) {
        if (row.Phase is { } phase) {
            return $" phase sequence={phase.Sequence}";
        }
        var domain = row.EffectiveDomain switch {
            WorldStateDomain.Slot => "domain=slot",
            WorldStateDomain.Keys => "domain=keys",
            WorldStateDomain.KeysOf keysOf => $"domain=keysOf row={keysOf.Row} ordered={keysOf.Ordered}",
            WorldStateDomain.CellsOf cellsOf => $"domain=cellsOf topology={cellsOf.Topology} empty={cellsOf.Empty}",
            WorldStateDomain.Ring ring => $"domain=ring capacity={ring.Capacity} empty={ring.Empty} cursor={row.HistoryCursor} held={Math.Min(row.HistoryCursor, ring.Capacity)}",
            var other => throw new InvalidOperationException($"unknown state domain '{other.GetType().Name}'"),
        };
        return row.ValuesFrom is { } valuesFrom ? $" {domain} valuesFrom={valuesFrom}" : $" {domain}";
    }
}
