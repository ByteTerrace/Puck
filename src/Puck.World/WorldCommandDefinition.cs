using Puck.Commands;

namespace Puck.World;

/// <summary>Owns the shared construction rules for simulation-routed world command definitions.</summary>
internal static class WorldCommandDefinition {
    /// <summary>Creates a simulation-routed row mutation command, including inline JSON parsing and submission.</summary>
    public static CommandDefinition Simulation(string name, string description, Func<CommandContext, WireArgs, CommandResult> handler) =>
        CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: name,
            description: description,
            handler: handler,
            routing: CommandRouting.Simulation
        );
}
