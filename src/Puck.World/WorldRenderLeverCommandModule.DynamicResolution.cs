using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldRenderLeverCommandModule {
    private CommandDefinition DynamicResolutionCommand() => CommandDefinition.WithWireArgs(
        bindability: CommandBindability.Unbindable,
        name: "world.dynamic-resolution",
        description: "Adapts each view's render extent inside its current ceiling: world.dynamic-resolution [on|off]. Presentation only; no resource rebuild or history reset. No argument reports the current state.",
        handler: (context, args) => {
            if (args.Count == 0) {
                return new CommandResult(Output: DynamicResolutionEcho());
            }
            if ((args.Count != 1) || (ParseOnOff(token: args[0]) is not { } enabled)) {
                return CommandResult.Error(output: "[world.dynamic-resolution: expected on|off]");
            }
            return SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.DynamicResolution,
                a: (enabled ? 1.0 : 0.0), formatEcho: () => new CommandResult(Output: DynamicResolutionEcho()));
        });
    private string DynamicResolutionEcho() => $"[world.dynamic-resolution: {(settings.DynamicResolution ? "on" : "off")}]";
}
