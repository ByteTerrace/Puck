using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

public sealed partial class WorldRenderLeverCommandModule {
    private CommandDefinition BloomCommand() => CommandDefinition.WithWireArgs(
        bindability: CommandBindability.Bindable, name: "world.bloom",
        description: "Camera scatter: world.bloom [on|off|auto|toggle]; scale <0..1> [seat]; quality off|low|high|auto [view]. Status reports effective scatter, radius, quality and bypass reasons. world.debug-view bloom shows scatter alone.",
        handler: (context, args) => {
            if (args.Count == 0) { return BloomEcho(); }
            WorldSessionLever lever;

            if (args.Is(index: 0, value: "scale") && (args.Count is 2 or 3) &&
                CommandArgs.TryParseFloat(text: args[1], value: out var scale) && float.IsFinite(f: scale) && (scale >= 0f) && (scale <= 1f)) {
                var seat = 0;

                if ((args.Count == 3) && (!int.TryParse(args[2], CultureInfo.InvariantCulture, out seat) || (seat < 0) || (seat >= PlayerRoster.MaxSlots))) {
                    return CommandResult.Error(output: "[world.bloom: invalid seat]");
                }
                lever = new(WorldSection.Render, WorldSessionLevers.BloomScale, scale, Seat: seat);
            } else if (args.Is(index: 0, value: "quality") && (args.Count is 2 or 3)) {
                var automatic = args.Is(index: 1, value: "auto");

                if (!automatic && (!Enum.TryParse<BloomQuality>(args[1], true, out _) || !Enum.IsDefined(value: Enum.Parse<BloomQuality>(args[1], true)))) {
                    return CommandResult.Error(output: "[world.bloom: expected quality off|low|high|auto [view]]");
                }
                var view = ((args.Count == 3) ? args[2].ToString() : "*");

                lever = new(WorldSection.Render, WorldSessionLevers.BloomQuality,
                    (automatic ? -1d : (double)Enum.Parse<BloomQuality>(args[1], true)), View: view);
            } else if ((args.Count == 1) && (args.Is(index: 0, value: "on") || args.Is(index: 0, value: "off") || args.Is(index: 0, value: "auto") || args.Is(index: 0, value: "toggle"))) {
                var enabled = (settings.ResolveBloom(server.Definition.Render).Scatter > 0f);

                lever = new(WorldSection.Render, WorldSessionLevers.Bloom,
                    (args.Is(index: 0, value: "auto") ? -1d : ((args.Is(index: 0, value: "on") || (args.Is(index: 0, value: "toggle") && !enabled)) ? 1d : 0d)));
            } else { return CommandResult.Error(output: "[world.bloom: expected on|off|auto|toggle, scale <0..1> [seat], or quality off|low|high|auto [view]]"); }
            link.SubmitSessionLever(lever: lever, principal: context.Principal);
            return BloomEcho();
        });
    private CommandResult BloomEcho() {
        var output = new StringBuilder();

        output.Append(CultureInfo.InvariantCulture, $"[world.bloom: mode={(settings.BloomOverride?.ToString() ?? "auto")} quality={settings.BloomQualityOf(view: "*")} seat0-scale={settings.BloomScale(seat: 0):0.###}");
        if ((renderProbe.Root?.Runtime is { } runtime) && (renderProbe.Views is { } mappings)) {
            foreach (var instance in runtime.Instances.Instances) {
                if ((runtime.NodeOf(instance: instance.Name) is not { } node) || !node.PassLabels.Contains(value: "sdf.world$composite") ||
                    (mappings.ViewOf(instance: instance.Name) is not { } mapped) || (mapped.Residency.Frame is not { } frame) ||
                    (mapped.View >= frame.Views.Count)) { continue; }
                var view = frame.Views[mapped.View];
                var authored = view.Bloom;
                var scatter = ((authored?.Scatter ?? 0f) * (1f - Math.Clamp(view.ScreenFidelity, 0f, 1f)));
                var reason = ((view.Unexposed || view.Quality.GeometryOnly) ? "transport" :
                    (((mapped.Residency.DebugMode != 0) && (mapped.Residency.DebugMode != DebugViewModes.Bloom)) ? "diagnostic" :
                    ((view.ScreenFidelity >= 1f) ? "screen-fidelity" : ((view.BloomQuality == BloomQuality.Off) ? "quality-off" :
                    ((scatter <= 0f) ? "disabled-or-player-scale" : "none")))));
                var levels = ((reason == "none") ? ((view.BloomQuality == BloomQuality.High) ? 6 : 5) : 0);

                output.Append(CultureInfo.InvariantCulture, $"; view={instance.Name} scatter={((reason == "none") ? scatter : 0f):0.####} radius={(authored?.Radius ?? .08f):0.###} cutoff={(authored?.Cutoff ?? 0f):0.###} quality={view.BloomQuality} levels={levels} bypass={reason}");
                if (levels > 0) {
                    var divisor = ((view.BloomQuality == BloomQuality.High) ? 2d : 4d);

                    output.Append(value: " mips=");
                    for (var level = 0; (level < levels); level++, divisor *= 2d) {
                        if (level != 0) { output.Append(value: ','); }
                        output.Append(CultureInfo.InvariantCulture, $"{Math.Max(val1: 1, val2: Math.Round(a: (node.Extent.Width / divisor)))}x{Math.Max(val1: 1, val2: Math.Round(a: (node.Extent.Height / divisor)))}");
                    }
                }
            }
        } else { output.Append(value: "; bypass=no-rendered-view"); }
        return new CommandResult(Output: output.Append(value: ']').ToString());
    }
}
