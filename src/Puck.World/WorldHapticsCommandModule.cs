using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Read-back surface for world-authored pad haptics (<see cref="WorldHapticDirector"/>): <c>haptics.state</c> reports
/// per-seat authored row count, pad attachment/output availability, and the monotone pulse count and last submission.
/// </summary>
internal sealed class WorldHapticsCommandModule(WorldHapticDirector director) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "haptics.state",
            description: "Echoes each local joined seat's live pad-haptics status: how many authored haptics rows target the seat, whether a gamepad and IGamepadOutput handle are resolved, total submitted pulses (monotone), and the most recent pulse (row id, tick, intensities, duration, accepted). A query — always echoes.",
            handler: (context, args) => ((CommandResult.RequireNoArguments(
                args: args,
                verb: "haptics.state"
            ) is { } refusal)
                ? refusal
                : new CommandResult(Output: director.Describe()))
        );
    }
}
