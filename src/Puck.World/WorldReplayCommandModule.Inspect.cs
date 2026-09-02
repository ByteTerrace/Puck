using System.Text.Json;
using Puck.Commands;

namespace Puck.World;

/// <summary>The <c>replay.inspect</c> half of the replay console surface — the tape's read-back (see
/// <see cref="WorldReplayInspector"/>).</summary>
internal sealed partial class WorldReplayCommandModule {
    private const string InspectUsage = "[replay.inspect: usage — replay.inspect <name> [<from>-<to>] [--all] [--poses]]";

    private CommandResult Inspect(WireArgs args) {
        if (args.Count < 1) {
            return CommandResult.Error(output: InspectUsage);
        }

        var name = args[0].ToString();

        if (!WorldReplayTape.IsValidName(name: name)) {
            return CommandResult.Error(output: "[replay.inspect: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }

        var from = 0;
        var to = int.MaxValue;
        var all = false;
        var poses = false;

        for (var index = 1; (index < args.Count); index++) {
            if (args.Is(
                index: index,
                value: "--all"
            )) {
                all = true;
            } else if (args.Is(
                index: index,
                value: "--poses"
            )) {
                poses = true;
            } else if (TryParseRange(
                from: out from,
                text: args[index],
                to: out to
            )) {
                // A `<from>-<to>` range, both digits, from <= to.
            } else {
                return CommandResult.Error(output: $"[replay.inspect: unrecognized argument '{args[index]}' — {InspectUsage[1..]}");
            }
        }

        try {
            var loaded = WorldReplayInspector.Load(name: name);
            var tickCount = loaded.Recording.TickCount;

            if (from >= tickCount) {
                return CommandResult.Error(output: $"[replay.inspect: '{name}' has {tickCount} tick(s) — from {from} is beyond the tape; the printable range is 0-{Math.Max(
                    val1: (tickCount - 1),
                    val2: 0
                )}]");
            }

            var lines = m_inspector.Inspect(
                all: all,
                from: from,
                loaded: in loaded,
                name: name,
                poses: poses,
                to: to
            );

            return new CommandResult(Output: string.Join(
                separator: Environment.NewLine,
                values: lines
            ));
        } catch (FileNotFoundException) {
            return CommandResult.Error(output: $"[replay.inspect: no replay named '{name}' — replay.list shows what's saved]");
        } catch (WorldReplayCodecException exception) {
            return CommandResult.Error(output: $"[replay.inspect: '{name}' hit a host-side codec bug (not a corrupt tape) — {exception.Message}]");
        } catch (InvalidOperationException exception) {
            return CommandResult.Error(output: $"[replay.inspect: '{name}' — {exception.Message}]");
        } catch (Exception exception) when ((exception is InvalidDataException or IOException or JsonException)) {
            return CommandResult.Error(output: $"[replay.inspect: '{name}' is unreadable/corrupt or its re-drive refused — {exception.Message}]");
        }
    }
    // `<from>-<to>`: two digit runs around one '-', from <= to. Anything else is not a range (and falls through to the
    // caller's unrecognized-argument refusal), so a stray token never silently reads as tick 0.
    private static bool TryParseRange(ReadOnlySpan<char> text, out int from, out int to) {
        from = 0;
        to = int.MaxValue;

        var dash = text.IndexOf(value: '-');

        if (
            (dash <= 0) ||
            (dash == (text.Length - 1))
        ) {
            return false;
        }

        if (
            !CommandArgs.TryParseUnsignedDigits(
            text: text[..dash],
            value: out var low
        ) ||
            !CommandArgs.TryParseUnsignedDigits(
            text: text[(dash + 1)..],
            value: out var high
        ) ||
            (low > high) ||
            (high > int.MaxValue)
        ) {
            return false;
        }

        from = ((int)low);
        to = ((int)high);

        return true;
    }
}
