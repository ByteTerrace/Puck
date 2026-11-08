using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldViewCommandModule {
    private static readonly CommandForms WorldViewPointerForms = CommandForms.Of(verb: "world.view.pointer", "[<client-x> <client-y>|clear]");
    private static readonly CommandForms WorldViewPickForms = CommandForms.Of(verb: "world.view.pick", "[<view-or-instance>] [<x> <y>]", "[<view-or-instance>] finding <id>");
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
            return WorldViewPointerForms.Refuse();
        }
        return (feed.TryOverridePointer(point: new Vector2(x: x, y: y))
            ? new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.view.pointer: override={x},{y}]"))
            : CommandResult.Error(output: $"[world.view.pointer: ({x}, {y}) is not a finite point inside the client extent]"));
    }
    // The answer of a completed pick, one line.
    private static string Answer(string instance, SdfPickResult result) {
        var target = (result.Target as WorldPickTarget);
        // A screen surface carries its screen index in its material id, above the screen sentinel.
        var screen = ((result.Material > SdfProgramBuilder.ScreenMaterialId) ? $" screen={(result.Material - SdfProgramBuilder.ScreenMaterialId - 1)}" : string.Empty);

        return string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"[world.view.pick: instance={instance} request={result.Request} pixel={result.X},{result.Y} kind={((uint)result.Kind)} source={result.Source} material={result.Material}{screen} placement={(target?.Placement ?? "none")} body={(target?.BodyIndex?.ToString(provider: CultureInfo.InvariantCulture) ?? "none")} prototype={(target?.Prototype ?? "none")} materialName={(result.MaterialName ?? "none")}]");
    }
    private CommandResult Pick(CommandContext context, WireArgs args, TextCommandSession? session) {
        if (args.Count > 3) {
            return WorldViewPickForms.Refuse(args: in args);
        }
        // A view is named by its first argument when one or three are given; with none, or just a point, the pick
        // addresses the presented world view (the first seat's rendered instance).
        var named = (args.Count is 1 or 3);
        var rest = (named ? 1 : 0);
        var presented = (viewports?.Seat(0).RenderInstance ?? WorldViewGraphs.WorldInstance);
        var instance = (named ? args[0].ToString() : "primary");
        if (instance == "primary") { instance = presented; }

        if (args.Count == 3 && args.Is(1, "finding")) {
            if (!context.Principal.IsAuthority) {
                return CommandResult.Error("[world.view.pick: diagnostic findings require the operator]");
            }
            if (diagnostics?.Find(args[2].ToString()) is not { } finding || finding.Identity.Instance != instance) {
                return CommandResult.Error(output: $"[world.view.pick: no finding '{args[2]}' in instance '{instance}']");
            }
            return new CommandResult(Output: finding.ToJson());
        }

        if (graphs?.FindPicker(instance: instance) is not { } picker) {
            return CommandResult.Error(output: $"[world.view.pick: no rendered SDF view '{instance}' (the presented view is '{presented}'; world.view.panes names it)]");
        }
        if (args.Count >= 2) {
            if (!float.TryParse(s: args[rest].ToString(), provider: CultureInfo.InvariantCulture, result: out var x) ||
                !float.TryParse(s: args[rest + 1].ToString(), provider: CultureInfo.InvariantCulture, result: out var y) ||
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
                    // The picker keeps this request's answer apart from the hover's result until it is taken here.
                    answer ??= picker.TakeAnswer(request: request);

                    return ((answer is null) && (picker.RequestIdentity == request) && picker.Awaiting);
                },
                released: deadlinePassed => {
                    answer ??= picker.TakeAnswer(request: request);
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
