using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The editor's presentation-only inspector and observational timestamp toggles.</summary>
internal sealed partial class WorldInspectionCommandModule(WorldEditorSeats seats, WorldGpuTiming timing, WorldRenderProbe? probe = null, WorldInspector? inspector = null, WorldCursorFeed? cursor = null) : ICommandModule {
    private CommandResult Inspect(CommandContext context, WireArgs args) {
        if (inspector is null) { return CommandResult.Error(output: "[world.inspect: requires GPU presentation]"); }
        if (args.Count != 0) {
            if ((args.Count != 1) || (!args.Is(index: 0, value: "on") && !args.Is(index: 0, value: "off"))) {
                return CommandResult.Usage(form: "[on|off]", verb: "world.inspect");
            }
            seats.SetInspector(slot: context.Slot, enabled: args.Is(index: 0, value: "on"));
        }
        return new CommandResult(Output: inspector.Describe(slot: context.Slot));
    }
    private CommandResult Timing(CommandContext context, WireArgs args) {
        if (!timing.Available) { return CommandResult.Error(output: "[world.gpu-timing: requires a composed GPU render graph]"); }
        if (args.Count != 0) {
            if ((args.Count != 1) || (!args.Is(index: 0, value: "on") && !args.Is(index: 0, value: "off"))) {
                return CommandResult.Usage(form: "[on|off]", verb: "world.gpu-timing");
            }
            timing.Set(enabled: args.Is(index: 0, value: "on"));
        }
        return new CommandResult(Output: timing.Describe());
    }

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(name: "world.cost", description: "Reads live placement cost: world.cost [<placement>]; world.cost top [<n>]. Prints exclusive words and separate shared program overhead.",
            handler: Cost, routing: CommandRouting.Immediate, bindability: CommandBindability.Bindable);
        yield return CommandDefinition.WithWireArgs(name: "world.inspect", description: "Shows the acting seat's pointer, camera, counts and reload diagnostics: world.inspect on|off; bare prints the same text as its panel.",
            handler: Inspect, routing: CommandRouting.Immediate, bindability: CommandBindability.Bindable);
        yield return CommandDefinition.WithWireArgs(name: "world.gpu-timing", description: "Enables observational per-pass timestamps: world.gpu-timing on|off; bare prints completed window means in milliseconds. Off by default; timings never judge correctness or choose rendering quality.",
            handler: Timing, routing: CommandRouting.Immediate, bindability: CommandBindability.Bindable);
    }
}
