using System.Globalization;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Session-only before-and-after controls for the acting seat.</summary>
/// <param name="comparison">The held frames and display controls.</param>
/// <param name="capture">The ordinary capture and result-reporting seam.</param>
public sealed class WorldCompareCommandModule(WorldFrameComparison comparison, WorldCompareCapture capture) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "world.compare",
            bindability: CommandBindability.Bindable,
            routing: CommandRouting.Immediate,
            valueKind: CommandValueKind.Axis1D,
            description: "Holds and compares the acting seat's displayed frame: world.compare hold|wipe [position]|split|diff|off. Wipe position is in 0..1 and defaults to the previous position (initially 0.5). A bound Axis1D value sets the wipe position without a capture. A typed hold or mode change captures through the ordinary live-root capture gate and reports changed-pixels at the shared two-code threshold. A text session waits for that capture before its next line. Off releases comparison GPU work and keeps the held CPU image for reuse. Bare, reports the latest comparison.",
            handler: (context, args) => {
                var slot = context.Slot;

                if ((context.Origin == CommandOrigin.Binding) && (args.Count == 0)) {
                    var position = context.Value.AsAxis1D;

                    if (!float.IsFinite(f: position) || (position < 0f) || (position > 1f)) {
                        return CommandResult.Error(output: "[world.compare: wipe position must be in 0..1]");
                    }
                    try { comparison.SetMode(mode: WorldCompareMode.Wipe, slot: slot, wipe: position); } catch (InvalidOperationException error) { return CommandResult.Error(output: $"[world.compare: {error.Message}]"); }
                    return CommandResult.None;
                }
                if (args.Count == 0) { return capture.Echo(slot: slot); }
                var hold = args.Is(index: 0, value: "hold");
                var mode = args[0].ToString() switch {
                    "off" => WorldCompareMode.Off,
                    "wipe" => WorldCompareMode.Wipe,
                    "split" => WorldCompareMode.Split,
                    "diff" => WorldCompareMode.Diff,
                    _ => ((WorldCompareMode)byte.MaxValue),
                };

                if ((args.Count > 2) || ((args.Count == 2) && (mode != WorldCompareMode.Wipe)) || (!hold && !Enum.IsDefined(value: mode))) {
                    return CommandResult.Usage(form: "hold|wipe [position]|split|diff|off", verb: "world.compare");
                }
                float? wipe = null;

                if (args.Count == 2) {
                    if (!float.TryParse(s: args[1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var position) ||
                        !float.IsFinite(f: position) || (position < 0f) || (position > 1f)) {
                        return CommandResult.Error(output: "[world.compare: wipe position must be in 0..1]");
                    }
                    wipe = position;
                }
                if (!hold && (mode == WorldCompareMode.Off)) {
                    comparison.SetMode(slot: slot, mode: mode);
                    return capture.Echo(slot: slot);
                }
                var result = capture.Request(hold: hold, slot: slot);

                if (result.IsError) { return result; }
                if (!hold) { comparison.SetMode(mode: mode, slot: slot, wipe: wipe); }
                if (result.Settlement is { } settlement) {
                    context.TextSession?.HoldWhile(hold: () => !settlement.IsSettled);
                }
                return result;
            });
    }
}
