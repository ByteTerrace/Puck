using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Read-back surface for world-authored keyboard lamps (<see cref="WorldKeyLightDirector"/>): <c>lights.state</c>
/// reports each seat's legend as the composer painted it and the lamp arrays being written.
/// </summary>
internal sealed class WorldKeyLightCommandModule(WorldKeyLightDirector director) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "lights.state",
            description: "Echoes each local seat's keyboard lamp legend from its world's keyLights section: how many rows target the seat, how many keys they light, the flashes fired (monotone), and every key off its idle color with the color the composer painted (source:#RRGGBB, a flash mid-fade included); then the lamp arrays present, how many the host holds from their firmware, the frames written (monotone) and the seat whose legend they show. arrays=off on a host that opens no physical device. A query — always echoes.",
            handler: (context, args) => ((CommandResult.RequireNoArguments(
                args: args,
                verb: "lights.state"
            ) is { } refusal)
                ? refusal
                : new CommandResult(Output: director.Describe()))
        );
    }
}
