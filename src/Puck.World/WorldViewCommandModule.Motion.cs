using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldViewCommandModule {
    // world.view.motion: reads, retargets or resets the presenter's per-frame trace of one seat.
    private CommandResult Motion(WireArgs args) {
        if (motionTrace is not { } trace) {
            return CommandResult.Error(output: "[world.view.motion: this presentation records no motion trace]");
        }
        if ((args.Count == 1) && args.Is(index: 0, value: "reset")) {
            trace.Reset();

            return new CommandResult(Output: "[world.view.motion: reset]");
        }
        if ((args.Count == 2) && args.Is(index: 0, value: "seat")) {
            if (!args.TryInt(index: 1, value: out var seat) || (seat < 1) || (seat > PlayerRoster.MaxSlots)) {
                return CommandResult.Error(output: $"[world.view.motion: expected a seat number 1..{PlayerRoster.MaxSlots}]");
            }

            trace.Slot = PlayerRoster.SlotFromDisplay(number: seat);

            return new CommandResult(Output: $"[world.view.motion: tracing seat {seat}]");
        }

        var frames = 120;

        if ((args.Count > 1) || ((args.Count == 1) && (!args.TryInt(index: 0, value: out frames) || (frames < 3)))) {
            return CommandResult.Usage(form: "[frames] | seat <n> | reset", verb: "world.view.motion");
        }

        return new CommandResult(Output: $"[world.view.motion: {trace.Describe(frames: Math.Min(val1: frames, val2: WorldSeatMotionTrace.Capacity))}]");
    }
}
