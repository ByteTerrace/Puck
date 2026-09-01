using System.Text.Json;
using Puck.Commands;

namespace Puck.World;

// replay.drive / replay.fork — the live-drive half of the replay surface, plus replay.cancel's drive arm.
internal sealed partial class WorldReplayCommandModule {
    private CommandResult BeginDrive(string verb, string name, int? toTick, string? forkName) {
        try {
            if (!m_tape.TryBeginDrive(
                documentPath: m_instances.Boot?.SourcePath,
                forkName: forkName,
                name: name,
                refusal: out var refusal,
                toTick: toTick
            )) {
                return CommandResult.Error(output: $"[{verb}: refused — {refusal}]");
            }
        } catch (FileNotFoundException) {
            return CommandResult.Error(output: $"[{verb}: no replay named '{name}' — replay.list shows what's saved]");
        } catch (WorldReplayCodecException exception) {
            return CommandResult.Error(output: $"[{verb}: '{name}' hit a host-side codec bug (not a corrupt tape) — {exception.Message}]");
        } catch (Exception exception) when ((exception is InvalidDataException or IOException or JsonException)) {
            return CommandResult.Error(output: $"[{verb}: '{name}' cannot be driven — {exception.Message}]");
        }

        var progress = m_tape.DriveProgress!.Value;

        return new CommandResult(Output: ((forkName is { } fork)
            ? $"[replay.fork: fast-forwarding '{name}' to tick {progress.Target} of {progress.TapeTicks}, then recording '{fork}' live from there — seats return to live input at the handover; replay.stop persists the child]"
            : $"[replay.drive: driving '{name}' to tick {progress.Target} of {progress.TapeTicks} at the recorded rate — local seat input is masked until it ends; replay.status reports progress, replay.cancel ends it early]"));
    }
    private CommandResult CancelDrive() {
        var progress = m_tape.DriveProgress!.Value;
        var name = m_tape.CancelDrive();

        return new CommandResult(Output: $"[replay.cancel: ended the drive of '{name}' at tick {progress.Cursor} of {progress.Target} — the world stays there, seats are live again{((progress.ForkName is { } fork)
            ? $", fork '{fork}' abandoned"
            : "")}]");
    }
    private CommandResult Drive(WireArgs args) {
        if (
            ((args.Count != 1) && (args.Count != 3)) ||
            ((args.Count == 3) && !args.Is(index: 1, value: "to"))
        ) {
            return CommandResult.Error(output: "[replay.drive: usage — replay.drive <name> [to <tick>]]");
        }

        if (!TryName(args: in args, index: 0, verb: "replay.drive", name: out var name, refusal: out var nameRefusal)) {
            return nameRefusal;
        }

        int? toTick = null;

        if (args.Count == 3) {
            if (
                !args.TryInt(index: 2, value: out var tick) ||
                (tick < 1)
            ) {
                return CommandResult.Error(output: "[replay.drive: <tick> must be a positive integer — the number of recorded ticks to drive]");
            }

            toTick = tick;
        }

        return BeginDrive(
            forkName: null,
            name: name,
            toTick: toTick,
            verb: "replay.drive"
        );
    }
    private CommandResult Fork(WireArgs args) {
        if (args.Count != 3) {
            return CommandResult.Error(output: "[replay.fork: usage — replay.fork <name> <tick> <new>]");
        }

        if (!TryName(args: in args, index: 0, verb: "replay.fork", name: out var name, refusal: out var nameRefusal)) {
            return nameRefusal;
        }

        if (
            !args.TryInt(index: 1, value: out var tick) ||
            (tick < 1)
        ) {
            return CommandResult.Error(output: "[replay.fork: <tick> must be a positive integer — how many of the parent's leading ticks the child copies before recording live]");
        }

        if (!TryName(args: in args, index: 2, verb: "replay.fork", name: out var forkName, refusal: out var forkRefusal)) {
            return forkRefusal;
        }

        if (string.Equals(
            a: name,
            b: forkName,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            return CommandResult.Error(output: $"[replay.fork: <new> must differ from '{name}' — a fork never overwrites its parent]");
        }

        return BeginDrive(
            forkName: forkName,
            name: name,
            toTick: tick,
            verb: "replay.fork"
        );
    }
    // The tape-name argument grammar the two drive verbs share.
    private static bool TryName(in WireArgs args, int index, string verb, out string name, out CommandResult refusal) {
        name = args[index].ToString();

        if (!WorldReplayTape.IsValidName(name: name)) {
            refusal = CommandResult.Error(output: $"[{verb}: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");

            return false;
        }

        refusal = default;

        return true;
    }
}
