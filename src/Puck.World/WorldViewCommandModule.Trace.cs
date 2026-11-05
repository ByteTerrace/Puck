using System.Globalization;
using Puck.Abstractions;
using Puck.Commands;

namespace Puck.World;

internal sealed partial class WorldViewCommandModule {
    private static readonly CommandForms WorldViewTraceForms = CommandForms.Of(verb: "world.view.trace", "begin <firstTick> <lastTick> [measureFirstTick]", "--json", "write <path>", "end");
    private CommandResult Trace(WireArgs args) {
        if (presentationProbe is not { } probe) { return CommandResult.Error(output: "[world.view.trace: no presentation observer is registered]"); }
        if (args.Count == 1 && args.Is(index: 0, value: "--json")) { return new CommandResult(Output: probe.ReadJson()); }
        if (args.Count == 1 && args.Is(index: 0, value: "end")) {
            probe.End();
            return new CommandResult(Output: "[world.view.trace: ended]");
        }
        if (args.Count >= 2 && args.Is(index: 0, value: "write")) {
            try {
                var path = PuckPaths.Normalize(path: args.Tail(start: 1));
                File.WriteAllText(path: path, contents: probe.ReadJson());
                return new CommandResult(Output: $"[world.view.trace: wrote {path}]");
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
                return CommandResult.Error(output: $"[world.view.trace: {exception.Message}]");
            }
        }
        if (args.Count is 3 or 4 && args.Is(index: 0, value: "begin") &&
            ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var first) &&
            ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var last)) {
            ulong? measureFirst = null;
            if (args.Count == 4) {
                if (!ulong.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var measured)) {
                    return CommandResult.Error(output: "[world.view.trace: measureFirstTick must be an unsigned tick]");
                }
                measureFirst = measured;
            }
            try { probe.Begin(firstTick: first, lastTick: last, measureFirstTick: measureFirst); }
            catch (ArgumentOutOfRangeException exception) { return CommandResult.Error(output: $"[world.view.trace: {exception.Message}]"); }
            return new CommandResult(Output: $"[world.view.trace: armed {first}..{last} measure={measureFirst ?? first}]");
        }
        return WorldViewTraceForms.Refuse(args: in args);
    }
}
