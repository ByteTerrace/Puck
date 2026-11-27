using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Reads the acting seat's own-body prediction (<see cref="WorldSeatPrediction"/>) and switches its replay check,
/// which replays every continued prediction from its continuation and counts the tips that differ.</summary>
internal sealed class WorldPredictionCommandModule(WorldSeatPrediction prediction) : ICommandModule {
    private static readonly CommandForms WorldPredictionForms = CommandForms.Of(verb: "world.prediction", "check on|off");

    private CommandResult Prediction(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            if ((args.Count != 2) || !args.Is(index: 0, value: "check") || (!args.Is(index: 1, value: "on") && !args.Is(index: 1, value: "off"))) {
                return WorldPredictionForms.Refuse(args: in args);
            }
            prediction.CheckReplays = args.Is(index: 1, value: "on");
        }
        if (((uint)context.Slot) >= ((uint)PlayerRoster.MaxSlots)) {
            return CommandResult.Error(output: $"[world.prediction: slot {context.Slot} is not a seat]");
        }

        return new CommandResult(Output: $"[world.prediction: replay check {(prediction.CheckReplays ? "on" : "off")}; seat {context.Slot}: {prediction.Describe(slot: context.Slot)}]");
    }

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(name: "world.prediction", audience: CommandAudience.Host, description: $"Reads the acting seat's own-body prediction: the ticks it runs ahead, its steps, restores and corrections. {WorldPredictionForms.Spelling} replays every prediction that continued its path from its continuation and counts the tips that differ, the check that a path is the replay; each checked prediction costs a whole replay.", forms: WorldPredictionForms,
            handler: Prediction, routing: CommandRouting.Immediate, bindability: CommandBindability.Unbindable);
    }
}
