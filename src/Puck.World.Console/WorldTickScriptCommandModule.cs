using System.Globalization;
using System.Text.Json;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The console surface of <see cref="WorldTickScript"/>: <c>world.at</c>, which pins console lines to exact ticks of
/// the boot world, one at a time or as a whole script in one line, and reads back what is armed and what ran.
/// </summary>
/// <param name="script">The pinned lines this verb arms and reads back.</param>
public sealed class WorldTickScriptCommandModule(WorldTickScript script) : ICommandModule {
    private const string Usage = "world.at <tick>|+<ticks> <console line>, world.at {\"start\": <tick>|\"+<ticks>\", \"steps\": [{\"at\": <offset>, \"run\": \"<console line>\"}, …]}, world.at clear, world.at --json, or world.at";

    // A tick token: an absolute tick, or +N ticks after the completed tick.
    private bool TryTick(ReadOnlySpan<char> token, out ulong tick) {
        tick = 0UL;

        if (token.StartsWith(value: "+")) {
            if (!ulong.TryParse(provider: CultureInfo.InvariantCulture, result: out var offset, s: token[1..], style: NumberStyles.None) || (offset == 0UL)) {
                return false;
            }

            tick = (script.CompletedTick + offset);

            return true;
        }

        return ulong.TryParse(provider: CultureInfo.InvariantCulture, result: out tick, s: token, style: NumberStyles.None);
    }
    private CommandResult ArmScript(string json) {
        List<(ulong Tick, string Line)> lines = [];
        ulong start;

        try {
            using var document = JsonDocument.Parse(json: json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) {
                return CommandResult.Error(output: $"[world.at: a script is a JSON object — {Usage}]");
            }

            foreach (var member in root.EnumerateObject()) {
                if (member.Name is not ("start" or "steps")) {
                    return CommandResult.Error(output: $"[world.at: a script carries only start and steps, not '{member.Name}']");
                }
            }

            if (!root.TryGetProperty(propertyName: "start", value: out var startValue)) {
                start = (script.CompletedTick + 1UL);
            } else if (startValue.ValueKind == JsonValueKind.Number) {
                if (!startValue.TryGetUInt64(value: out start)) {
                    return CommandResult.Error(output: "[world.at: start is a whole tick]");
                }
            } else if ((startValue.ValueKind != JsonValueKind.String) || !TryTick(token: startValue.GetString(), tick: out start)) {
                return CommandResult.Error(output: "[world.at: start is a whole tick, or \"+<ticks>\" after the completed tick]");
            }

            if (!root.TryGetProperty(propertyName: "steps", value: out var steps) || (steps.ValueKind != JsonValueKind.Array) || (steps.GetArrayLength() == 0)) {
                return CommandResult.Error(output: "[world.at: a script needs a non-empty steps array]");
            }

            var index = 0;

            foreach (var step in steps.EnumerateArray()) {
                if (
                    (step.ValueKind != JsonValueKind.Object) ||
                    !step.TryGetProperty(propertyName: "at", value: out var at) ||
                    (at.ValueKind != JsonValueKind.Number) ||
                    !at.TryGetUInt64(value: out var offset) ||
                    !step.TryGetProperty(propertyName: "run", value: out var run) ||
                    (run.ValueKind != JsonValueKind.String) ||
                    step.EnumerateObject().Any(predicate: static member => (member.Name is not ("at" or "run")))
                ) {
                    return CommandResult.Error(output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.at: step {index} is not {{\"at\": <whole offset>, \"run\": \"<console line>\"}}]"));
                }

                lines.Add(item: (checked((start + offset)), run.GetString()!));
                index++;
            }
        } catch (JsonException exception) {
            return CommandResult.Error(output: $"[world.at: the script is not JSON ({exception.Message})]");
        } catch (OverflowException) {
            return CommandResult.Error(output: "[world.at: a step's tick overflows the tick range]");
        }

        if (script.Arm(lines: lines) is { } refusal) {
            return CommandResult.Error(output: $"[world.at: refused, nothing armed — {refusal}]");
        }

        var last = lines.Max(selector: static line => line.Tick);

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.at: armed {lines.Count} line(s) at ticks {lines.Min(selector: static line => line.Tick)}..{last} (now tick {script.CompletedTick}) — world.wait until {last} holds a session until the last has run]"));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: "world.at",
            description: "Pins console lines to exact ticks of the boot world, so a script gives the same ticks on every run however its lines arrive (stdin, a control attachment's calls). world.at <tick> <console line> runs the line in the console drain right after the boot world completes that tick and before the next tick steps (the point a line released by world.wait runs at); world.at +<ticks> <console line> pins it that many ticks after the tick the world has completed now. An Immediate line (a read such as world.state.hash or body.where, a capture such as world.screenshot <path.png>) sees the state of that tick, and a Simulation line (body.*, view.override, a state write) applies in the tick after it, whatever else that tick runs: every line of a tick runs in the order it was armed, none waits behind another. world.at {\"start\": <tick>|\"+<ticks>\", \"steps\": [{\"at\": <offset>, \"run\": \"<console line>\"}, …]} arms a whole script in one line, all or nothing, each step at start + at (start defaults to \"+1\"), and answers with the ticks it spans. A settled world.screenshot a line arms holds the rendered host at that tick until a frame of it serves the capture, so the still shows exactly that tick; its stderr line [still] <path> tick=<T> hash=<capture hash> pose=<pose hash> shown=<tick the frame shows> records it. Lines run as the console, so the verb is the operator's. world.wait until <tick> holds a session until a tick has completed, which is how a caller waits for a script. world.at lists the armed lines and the last ran ones with their answers; world.at --json returns tick, pending and ran arrays with each line\u0027s tick, command, status and output; world.at clear drops every armed line. Refused, nothing armed: a tick already completed, world.at, world.wait or quit as the pinned line, or more than 4096 armed lines. A paused world runs no armed line until it resumes, and a line runs on its tick only: pause, arm, then world.rate resume to pin a script to the very next ticks.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: script.Describe());
                }

                if ((args.Count == 1) && args.Is(index: 0, value: "--json")) {
                    return new CommandResult(Output: script.Describe(json: true));
                }

                if ((args.Count == 1) && args.Is(index: 0, value: "clear")) {
                    return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.at: cleared {script.Clear()} armed line(s)]"));
                }

                var head = args[0];

                if (head.StartsWith(value: "{")) {
                    return ArmScript(json: WorldCommandArguments.RawAfter(
                        args: in args,
                        context: context,
                        preserveQuotes: true,
                        tokens: 1
                    ));
                }

                if ((args.Count < 2) || !TryTick(tick: out var tick, token: head)) {
                    return CommandResult.Error(output: $"[world.at: expected {Usage}]");
                }

                var line = WorldCommandArguments.RawAfter(
                    args: in args,
                    context: context,
                    preserveQuotes: true,
                    tokens: 2
                );

                return ((script.Arm(line: line, tick: tick) is { } refusal)
                    ? CommandResult.Error(output: $"[world.at: refused — {refusal}]")
                    : new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.at: armed at tick {tick} (now tick {script.CompletedTick}): {line.Trim()}]")));
            }
        );
    }
}
