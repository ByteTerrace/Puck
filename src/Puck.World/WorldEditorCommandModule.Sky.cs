using System.Text;
using Puck.Commands;

namespace Puck.World;

public sealed partial class WorldEditorCommandModule {
    private IEnumerable<CommandDefinition> SkyVerbs() {
        yield return CommandDefinition.WithWireArgs(
            name: "world.sky.solo", description: "Solos one sky layer in the acting seat's presented world: world.sky.solo <layer>|off. Bare lists this seat's selections in every world. Survives reload; excluded from saves and replay.",
            bindability: CommandBindability.Bindable, routing: CommandRouting.Immediate,
            handler: (context, args) => SkyHandler(context, args, solo: true));
        yield return CommandDefinition.WithWireArgs(
            name: "world.sky.mute", description: "Mutes one sky layer in the acting seat's presented world: world.sky.mute <layer> on|off. Bare lists this seat's selections in every world. Solo overrides retained mutes until cleared. Excluded from saves and replay.",
            bindability: CommandBindability.Bindable, routing: CommandRouting.Immediate,
            handler: (context, args) => SkyHandler(context, args, solo: false));
    }
    private CommandResult SkyHandler(CommandContext context, WireArgs args, bool solo) {
        var verb = (solo ? "world.sky.solo" : "world.sky.mute");

        if (!TryEditWorld(context: context, refusal: out var refusal, verb: verb, world: out var world)) { return refusal; }
        _ = seats.SkyOf(context.Slot, world.Name, world.Definition);
        if (args.Count == 0) { return EchoSky(slot: context.Slot, verb: verb); }
        var muted = false;

        if ((solo && (args.Count != 1)) || (!solo && ((args.Count != 2) || !TryOnOff(token: args[1], value: out muted)))) {
            return CommandResult.Usage(form: (solo ? "<layer>|off" : "<layer> on|off"), verb: verb);
        }
        var layer = args[0].ToString();

        if (solo && (layer == "off")) {
            seats.SetSkySolo(context.Slot, world.Name, world.Definition, null);
        } else {
            var layers = world.Definition.Render.Sky?.Layers;

            if ((layers is null) || !layers.Any(predicate: row => (row.Name == layer))) {
                return CommandResult.Error(output: (((((((("[" + verb) + ": no layer '") + layer) + "' in '") + world.Name) + "'; layers: ") +
                    (((layers is null) || (layers.Count == 0)) ? "none" : string.Join(separator: ", ", values: layers.Select(selector: static row => row.Name)))) + "]"));
            }
            if (solo) { seats.SetSkySolo(context.Slot, world.Name, world.Definition, layer); } else { seats.SetSkyMute(context.Slot, world.Name, world.Definition, layer, muted); }
        }
        return EchoSky(slot: context.Slot, verb: verb);
    }
    private CommandResult EchoSky(int slot, string verb) {
        var result = new StringBuilder(value: ((("[" + verb) + ": seat ") + Client.PlayerRoster.DisplayNumber(slot: slot)));
        var any = false;

        foreach (var pair in seats.SkySelections(slot: slot)) {
            any = true;
            result.Append(value: "; world '").Append(value: pair.Key).Append(value: "' solo=").Append(value: (pair.Value.Solo ?? "off")).Append(value: " muted=");
            var muted = pair.Value.Muted;

            if (muted.Length == 0) { result.Append(value: "none"); }
            for (var index = 0; (index < muted.Length); index++) { if (index != 0) { result.Append(value: ','); } result.Append(value: muted[index]); }
        }
        if (!any) { result.Append(value: "; none"); }
        return new CommandResult(Output: result.Append(value: ']').ToString());
    }
}
