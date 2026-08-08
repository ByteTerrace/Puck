using System.Text.Json.Serialization.Metadata;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Owns the shared construction rules for simulation-routed world command definitions.</summary>
internal static class WorldCommandDefinition {
    /// <summary>Creates an unbindable simulation-routed command over raw wire arguments.</summary>
    public static CommandDefinition Simulation(string name, string description, Func<CommandContext, WireArgs, CommandResult> handler) =>
        CommandDefinition.WithWireArgs(bindability: CommandBindability.Unbindable, name: name, description: description, handler: handler, routing: CommandRouting.Simulation);

    /// <summary>Creates a simulation-routed row mutation command, including inline JSON parsing and submission.</summary>
    public static CommandDefinition Row<T>(string name, string description, JsonTypeInfo<T> info, Func<WorldPrincipal, T, WorldMutation> toMutation, IServerLink link) {
        return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: name,
            description: description,
            handler: (context, args) => {
                var raw = WorldCommandArguments.Raw(context: context, args: in args);

                if (!WorldJsonPayload.TryParse(json: raw, info: info, value: out var value, error: out var error)) {
                    return CommandResult.Error(output: $"[{name}: {error}]");
                }

                link.SubmitWorldMutation(mutation: toMutation(arg1: context.ActingPrincipal(), arg2: value));

                return CommandResult.None;
            },
            routing: CommandRouting.Simulation);
    }
}
