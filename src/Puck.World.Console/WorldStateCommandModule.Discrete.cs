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
                    lines.Add($"[world.topology '{topology.Name}' kind={topology.Kind} cells={compiled?.CellCount ?? topology.Width * topology.Depth * topology.Layers} directions={compiled?.DirectionCount ?? 0} wrap={topology.Wrap}]");
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
            var node = phase.Phases[phase.Current];
            return $" phase={node.Name} mode={node.Mode} active={phase.Active} ready={phase.Ready} sequence={phase.Sequence} round={phase.Round} direction={phase.Direction} skipped={phase.Skipped} deadline={WorldStateTransforms.Deadline(phase, server.Definition.SimulationRateHz)}";
        }
        if (row.Board is { } board) {
            return $" topology={board.Topology} empty={board.Empty}";
        }
        if (row.Zone is { } zone) {
            return $" tokens={zone.Tokens} ordered={zone.Ordered}";
        }
        return row.KeysFrom is { } keys ? $" keysFrom={keys} valuesFrom={row.ValuesFrom ?? "none"}" : row.Tokens is { } tokens ? $" tokenCapacity={tokens.Capacity}" : string.Empty;
    }
}
