using System.Text;
using Puck.Commands;

namespace Puck.World;

// The seat-facing half of world.history: which forms a seat may run and under what authority, the scrubber row's
// read-back and its two seek doors (scrub by fraction, and the held pointer drag), and the branch forms that take a kept
// future somewhere (switch and save).
public sealed partial class WorldHistoryCommandModule {
    // The forms a seat's binding may run. Each moves the shared timeline, so each is checked against control over
    // history under the pressing seat's principal; row only echoes the drawn row.
    private static bool IsSeatForm(WireArgs args) => (
        args.Is(index: 0, value: "step") ||
        args.Is(index: 0, value: "resume") ||
        args.Is(index: 0, value: "branch") ||
        args.Is(index: 0, value: "scrub")
    );
    // The dispatch-time check: the console runs every form; anyone reads the row; a seat runs the seat forms when it
    // holds control over history, and no other form.
    private CommandResult? Admission(WireArgs args, Principal principal) {
        if (
            (principal.Kind == PrincipalKind.Console) ||
            ((args.Count == 1) && args.Is(index: 0, value: "row"))
        ) {
            return null;
        }

        var form = ((args.Count == 0) ? "status" : args[0].ToString());

        if ((args.Count == 0) || !IsSeatForm(args: args)) {
            return CommandResult.Error(output: $"[world.history: {form} refused — an operator form; {principal.Describe()} is not the operator]");
        }

        return (m_history.TryAuthorize(principal: principal, refusal: out var denial)
            ? null
            : CommandResult.Error(output: $"[world.history: {form} refused — {denial}]"));
    }
    // A bound press seeks on its press and on every tick it is held, and does nothing on its release; a typed line is
    // one impulse, which seeks once.
    private CommandResult Drag(CommandContext context, WireArgs args) {
        if (
            ((context.Origin == CommandOrigin.Binding) && (context.Phase is CommandPhase.Completed or CommandPhase.Canceled)) ||
            (args.Count != 0) ||
            (m_history.Pointer is not { } pointer) ||
            !pointer.TryFraction(fraction: out var fraction, slot: context.Slot)
        ) {
            return CommandResult.None;
        }

        if (
            (context.Principal.Kind != PrincipalKind.Console) &&
            !m_history.TryAuthorize(principal: context.Principal, refusal: out var denial)
        ) {
            return CommandResult.Error(output: $"[world.history: drag refused — {denial}]");
        }

        if (
            !m_history.TryTickAt(fraction: fraction, tick: out var tick) ||
            (m_history.CursorTick == tick)
        ) {
            return CommandResult.None;
        }

        return Seek(target: tick, verb: "drag");
    }
    private CommandResult Row() {
        Span<ulong> keyframes = stackalloc ulong[WorldHistory.RowKeyframes];
        Span<WorldHistoryRowFork> forks = stackalloc WorldHistoryRowFork[WorldHistory.RowForks];

        if (!m_history.TryReadRow(
            forkCount: out var forkCount,
            forks: forks,
            keyframeCount: out var keyframeCount,
            keyframes: keyframes,
            window: out var window
        )) {
            return new CommandResult(Output: $"[world.history row: none — {(m_history.Status().Waiting ?? "the history is off")}]");
        }

        var output = new StringBuilder();

        _ = output.Append(value: $"[world.history row: window {window.Oldest}..{window.Head} | cursor {window.Cursor} | keyframes ");

        for (var index = 0; (index < keyframeCount); index++) {
            _ = output.Append(value: ((index == 0) ? string.Empty : ",")).Append(value: keyframes[index]);
        }

        _ = output.Append(value: " | forks ");

        if (forkCount == 0) {
            _ = output.Append(value: "none");
        }

        for (var index = 0; (index < forkCount); index++) {
            _ = output.Append(value: ((index == 0) ? string.Empty : ", ")).Append(value: $"{forks[index].Fork}..{forks[index].Head}");
        }

        return new CommandResult(Output: output.Append(value: ']').ToString());
    }
    private CommandResult Save(WireArgs args) {
        if (args.Count != 3) {
            return CommandResult.Error(output: "[world.history: usage — world.history save <branch> <tape>]");
        }

        var name = args[1].ToString();
        var tape = args[2].ToString();

        if (!m_history.TrySaveBranch(
            name: name,
            path: out var path,
            refusal: out var refusal,
            tapeName: tape,
            verdict: out var verdict
        )) {
            return CommandResult.Error(output: $"[world.history: save refused — {refusal}]");
        }

        return new CommandResult(Output: $"[world.history: saved branch '{name}' as tape '{tape}' ({path}) | forked from '{WorldHistory.SavedBranchParent}' | re-drive {verdict.Describe()}]") { IsError = !verdict.Match };
    }
    private CommandResult Scrub(WireArgs args) {
        if (
            (args.Count != 2) ||
            !args.TryFloat(index: 1, value: out var fraction) ||
            !float.IsFinite(f: fraction) ||
            (fraction < 0f) ||
            (fraction > 1f)
        ) {
            return CommandResult.Error(output: "[world.history: usage — world.history scrub <fraction>, 0 (the oldest tick) to 1 (the head)]");
        }

        // With no window there is no tick to name; the seek names why.
        return (m_history.TryTickAt(fraction: fraction, tick: out var tick)
            ? Seek(target: tick, verb: "scrub")
            : Seek(target: 0UL, verb: "scrub"));
    }
    private CommandResult Switch(WireArgs args) {
        if (args.Count != 2) {
            return CommandResult.Error(output: "[world.history: usage — world.history switch <branch>]");
        }

        var name = args[1].ToString();

        if (!m_history.TrySwitch(
            documentPath: DocumentPath,
            name: name,
            refusal: out var refusal,
            report: out var report
        )) {
            return CommandResult.Error(output: $"[world.history: switch refused — {refusal}]");
        }

        var kept = ((report.KeptTicks > 0)
            ? $"ticks {(report.ForkTick + 1UL)}..{(report.ForkTick + ((ulong)report.KeptTicks))} are kept as branch '{name}' in its place"
            : "the fork was the head, so no future is kept");
        var proof = (report.Matches
            ? $"authoritative 0x{report.LiveHash:X16} matches the branch's recording"
            : $"DIVERGED at tick {(report.DivergedAt ?? report.Head)} — live 0x{report.LiveHash:X16}, recorded 0x{report.RecordedHash:X16}");

        return new CommandResult(Output: $"[world.history: switch '{name}' {report.From} -> {report.Head} | forked after tick {report.ForkTick}, re-simulated {report.TicksResimulated} tick(s) | {kept} | {proof}]") { IsError = !report.Matches };
    }
}
