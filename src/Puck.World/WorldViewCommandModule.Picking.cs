using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldViewCommandModule {
    // How long a pick holds a session that carries no call deadline before it refuses as unanswered.
    private static readonly TimeSpan UnboundedPickWait = TimeSpan.FromSeconds(value: 10);

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
    // The answer of a completed pick, one line.
    private static string Answer(string instance, SdfPickResult result) {
        var target = (result.Target as WorldPickTarget);

        return string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"[world.view.pick: instance={instance} request={result.Request} pixel={result.X},{result.Y} kind={((uint)result.Kind)} source={result.Source} material={result.Material} placement={(target?.Placement ?? "none")} body={(target?.BodyIndex?.ToString(provider: CultureInfo.InvariantCulture) ?? "none")}]");
    }
    private CommandResult Pick(WireArgs args, TextCommandSession? session) {
        if (args.Count is not (1 or 3)) {
            return CommandResult.Usage(form: "<view-or-instance> [<x> <y>|finding <id>]", verb: "world.view.pick");
        }
        var instance = args[0].ToString();
        if (instance == "primary") { instance = viewports?.Seat(0).RenderInstance ?? WorldViewGraphs.WorldInstance; }

        if (args.Count == 3 && args.Is(1, "finding")) {
            if (diagnostics?.Find(args[2].ToString()) is not { } finding || finding.Identity.Instance != instance) {
                return CommandResult.Error(output: $"[world.view.pick: no finding '{args[2]}' in instance '{instance}']");
            }
            return new CommandResult(Output: finding.ToJson());
        }

        if (graphs?.FindPicker(instance: instance) is not { } picker) {
            return CommandResult.Error(output: $"[world.view.pick: no rendered SDF view '{instance}']");
        }
        if (args.Count == 3) {
            if (!float.TryParse(s: args[1].ToString(), provider: CultureInfo.InvariantCulture, result: out var x) ||
                !float.TryParse(s: args[2].ToString(), provider: CultureInfo.InvariantCulture, result: out var y) ||
                !float.IsFinite(f: x) || !float.IsFinite(f: y) || (x < 0) || (x >= 1) || (y < 0) || (y >= 1)) {
                return CommandResult.Error(output: "[world.view.pick: coordinates must be finite normalized numbers in [0,1)]");
            }
            var request = picker.Request(x: x, y: y);
            var pending = $"[world.view.pick: instance={instance} request={request} pending]";

            if (session is null) {
                return new CommandResult(Output: pending);
            }

            // The answer needs a rendered frame, not a simulation tick, so the call that asked holds until the frame
            // lands, a paused World included. The hold is bounded by the call's deadline, or by a fixed one in a
            // session that has none (a piped script), so a view that never renders cannot stall later commands.
            var bounded = (session.AnswerDeadline is null);
            SdfPickResult? answer = null;

            if (bounded) {
                session.AnswerDeadline = new TextAnswerDeadline(budget: UnboundedPickWait, clock: TimeProvider.System);
            }
            session.HoldWhile(
                hold: () => {
                    // The answer is taken as soon as it lands, before a later hover request can replace the result.
                    if ((answer is null) && (picker.Result is { } landed) && (landed.Request == request)) { answer = landed; }

                    return ((answer is null) && (picker.RequestIdentity == request) && picker.Awaiting);
                },
                released: deadlinePassed => {
                    if (answer is { } landed) {
                        return new CommandResult(Output: Answer(instance: instance, result: landed));
                    }
                    if (picker.RequestIdentity == request) { picker.Clear(); }

                    return CommandResult.Error(output: $"[world.view.pick: instance={instance} request={request} unanswered{(deadlinePassed ? $" before this call's answer was due; {(picker.Attached ? "the view rendered no frame in time" : "the view is not rendering")}" : "; the request was superseded or the view changed")}]");
                });
            if (bounded) {
                session.AnswerDeadline = null;
            }

            return new CommandResult(Output: pending);
        }
        if (picker.Result is not { } result) {
            return new CommandResult(Output: $"[world.view.pick: instance={instance} pending]");
        }

        return new CommandResult(Output: Answer(instance: instance, result: result));
    }
}
