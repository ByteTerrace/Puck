using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldViewCommandModule {
    private CommandResult Pointer(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            return DescribePointer();
        }
        if (context.Principal != Principal.Console) {
            return CommandResult.Error(output: "[world.view.pointer: override requires the host Console principal]");
        }
        if (cursorFeed is not { } feed) {
            return CommandResult.Error(output: "[world.view.pointer: requires a GPU presentation]");
        }
        if ((args.Count == 1) && (args[0].ToString() == "clear")) {
            _ = feed.TryOverridePointer(point: null);
            return new CommandResult(Output: "[world.view.pointer: override cleared]");
        }
        if ((args.Count != 2) ||
            !float.TryParse(s: args[0].ToString(), provider: CultureInfo.InvariantCulture, result: out var x) ||
            !float.TryParse(s: args[1].ToString(), provider: CultureInfo.InvariantCulture, result: out var y)) {
            return CommandResult.Usage(form: "[<client-x> <client-y>|clear]", verb: "world.view.pointer");
        }
        return (feed.TryOverridePointer(point: new Vector2(x: x, y: y))
            ? new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.view.pointer: override={x},{y}]"))
            : CommandResult.Error(output: "[world.view.pointer: expected finite coordinates inside the client extent]"));
    }
    private CommandResult Pick(WireArgs args) {
        if (args.Count is not (1 or 3)) {
            return CommandResult.Usage(form: "<instance> [<x> <y>]", verb: "world.view.pick");
        }
        var instance = args[0].ToString();

        if (graphs?.FindPicker(instance: instance) is not { } picker) {
            return CommandResult.Error(output: $"[world.view.pick: no rendered SDF view '{instance}']");
        }
        if (args.Count == 3) {
            if (!float.TryParse(s: args[1].ToString(), provider: CultureInfo.InvariantCulture, result: out var x) ||
                !float.TryParse(s: args[2].ToString(), provider: CultureInfo.InvariantCulture, result: out var y) ||
                !float.IsFinite(f: x) || !float.IsFinite(f: y) || (x < 0) || (x >= 1) || (y < 0) || (y >= 1)) {
                return CommandResult.Error(output: "[world.view.pick: coordinates must be finite normalized numbers in [0,1)]");
            }
            return new CommandResult(Output: $"[world.view.pick: instance={instance} request={picker.Request(x: x, y: y)} pending]");
        }
        if (picker.Result is not { } result) {
            return new CommandResult(Output: $"[world.view.pick: instance={instance} pending]");
        }
        var target = (result.Target as WorldPickTarget);

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"[world.view.pick: instance={instance} request={result.Request} pixel={result.X},{result.Y} kind={result.Kind} source={result.Source} material={result.Material} placement={(target?.Placement ?? "none")} body={(target?.BodyIndex?.ToString(provider: CultureInfo.InvariantCulture) ?? "none")}]"));
    }
}
