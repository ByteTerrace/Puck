using System.Globalization;
using System.Text;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The <c>world.history</c> console surface over the boot world's in-session <see cref="WorldHistory"/>: switch it on
/// and off, read its window and counted cost, seek and step the live world to any recorded tick, resume live input
/// from behind the head (discarding the future or keeping it as a named branch), diff the state at two ticks, and
/// replay an edit some ticks earlier to see where it would have changed the world. Every form is Immediate and runs
/// between steps on the step thread; the forms that print state or move the timeline answer the operator only.
/// </summary>
/// <param name="history">The boot world's history.</param>
/// <param name="instances">The host's instances, whose boot row a seek pauses and a resume releases, and whose
/// source path a restore and a shadow resolve content beside.</param>
public sealed class WorldHistoryCommandModule(WorldHistory history, WorldInstanceHost instances) : ICommandModule {
    private const string Usage = "[world.history: usage — world.history [on [<MiB>] | off | status | seek <tick> | step <±n> | diff <a> <b> [--json] | resume | branch <name> | replay-edit <n>]]";
    // The most diff lines the console prints before naming how many more the machine form carries.
    private const int MaxDiffLines = 24;

    private readonly WorldHistory m_history = history;
    private readonly WorldInstanceHost m_instances = instances;

    private string? DocumentPath => m_instances.Boot?.SourcePath;

    private static string Bytes(long bytes) => (bytes switch {
        >= (1024L * 1024L) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(bytes / (1024.0 * 1024.0)):0.0} MiB"),
        >= 1024L => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(bytes / 1024.0):0.0} KiB"),
        _ => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{bytes} B"),
    });
    private CommandResult Branch(WireArgs args) {
        if (args.Count != 2) {
            return CommandResult.Error(output: "[world.history: usage — world.history branch <name>]");
        }

        var name = args[1].ToString();

        if (!m_history.TryArmBranch(
            name: name,
            refusal: out var refusal
        )) {
            return CommandResult.Error(output: $"[world.history: branch refused — {refusal}]");
        }

        var cursor = m_history.CursorTick;
        var head = m_history.HeadTick;

        _ = m_instances.TryResume(
            name: WorldInstanceHost.BootInstanceName,
            reason: out _,
            wasPaused: out _
        );

        return new CommandResult(Output: $"[world.history: live input resumes at tick {cursor}; ticks {(cursor + 1UL)}..{head} are kept as branch '{name}' when the next tick lands]");
    }
    private CommandResult Diff(WireArgs args) {
        var json = ((args.Count == 4) && args.Is(index: 3, value: "--json"));

        if (
            ((args.Count != 3) && !json) ||
            !args.TryUnsignedDigits(index: 1, value: out var from) ||
            !args.TryUnsignedDigits(index: 2, value: out var to)
        ) {
            return CommandResult.Error(output: "[world.history: usage — world.history diff <a> <b> [--json]]");
        }

        if (!m_history.TryDiff(
            diff: out var diff,
            divergedAt: out var divergedAt,
            documentPath: DocumentPath,
            from: from,
            refusal: out var refusal,
            to: to
        )) {
            return CommandResult.Error(output: $"[world.history: diff refused — {refusal}]");
        }

        var warning = ((divergedAt is { } tick)
            ? $" | WARNING the re-simulation left the recording at tick {tick}"
            : string.Empty);

        if (json) {
            return new CommandResult(Output: Encoding.UTF8.GetString(bytes: diff!.ToCanonicalJson()).TrimEnd()) { IsError = (divergedAt is not null) };
        }

        var lines = diff!.DescribeLines(maxLines: MaxDiffLines);
        var output = new StringBuilder();

        for (var index = 0; (index < lines.Count); index++) {
            if (index > 0) {
                _ = output.Append(value: '\n');
            }

            _ = output.Append(value: "[world.history diff: ").Append(value: lines[index]).Append(value: ((index == 0) ? warning : string.Empty)).Append(value: ']');
        }

        return new CommandResult(Output: output.ToString()) { IsError = (divergedAt is not null) };
    }
    private CommandResult On(WireArgs args) {
        var budget = WorldHistory.DefaultBudgetBytes;

        if (args.Count == 2) {
            if (
                !args.TryUnsignedDigits(index: 1, value: out var mebibytes) ||
                (mebibytes == 0UL) ||
                (mebibytes > 65536UL)
            ) {
                return CommandResult.Error(output: "[world.history: the budget is a whole number of MiB, 1 to 65536]");
            }

            budget = ((((long)mebibytes) * 1024L) * 1024L);
        } else if (args.Count != 1) {
            return CommandResult.Error(output: "[world.history: usage — world.history on [<MiB>]]");
        }

        if (!m_history.TryOn(
            budgetBytes: budget,
            refusal: out var refusal
        )) {
            return CommandResult.Error(output: $"[world.history: on refused — {refusal}]");
        }

        return new CommandResult(Output: $"[world.history: on | budget {Bytes(bytes: budget)} | the next tick captures the first keyframe]");
    }
    private CommandResult ReplayEdit(WireArgs args) {
        if (
            (args.Count != 2) ||
            !args.TryInt(index: 1, value: out var ticksBack)
        ) {
            return CommandResult.Error(output: "[world.history: usage — world.history replay-edit <n>]");
        }

        if (!m_history.TryReplayEdit(
            documentPath: DocumentPath,
            refusal: out var refusal,
            report: out var report,
            ticksBack: ticksBack
        )) {
            return CommandResult.Error(output: $"[world.history: replay-edit refused — {refusal}]");
        }

        var output = new StringBuilder();
        var edits = string.Join(
            separator: "; ",
            values: report!.Edits
        );

        _ = output.Append(value: $"[world.history replay-edit: the edits at tick {report.EditTick} land at tick {report.LandedAt} — {edits}");

        if (report.ControlDivergedAt is { } control) {
            _ = output.Append(value: $" | REFUSED TO TRUST: the unedited re-simulation itself left the recording at tick {control}]");

            return CommandResult.Error(output: output.ToString());
        }

        if (report.DivergedAt is not { } diverged) {
            _ = output.Append(value: $" | no authoritative divergence through tick {report.EditTick}]");

            return new CommandResult(Output: output.ToString());
        }

        _ = output.Append(value: $" | diverges at tick {diverged}, {(diverged - report.LandedAt)} tick(s) after landing]");

        foreach (var line in report.Diff!.DescribeLines(maxLines: MaxDiffLines)) {
            _ = output.Append(value: "\n[world.history replay-edit: recorded vs edited ").Append(value: line).Append(value: ']');
        }

        return new CommandResult(Output: output.ToString());
    }
    private CommandResult Resume() {
        if (
            (m_history.CursorTick is { } cursor) &&
            (m_history.HeadTick is { } head) &&
            (cursor < head) &&
            !m_history.TryArmBranch(
                name: null,
                refusal: out var refusal
            )
        ) {
            return CommandResult.Error(output: $"[world.history: resume refused — {refusal}]");
        }

        _ = m_instances.TryResume(
            name: WorldInstanceHost.BootInstanceName,
            reason: out _,
            wasPaused: out var wasPaused
        );

        return new CommandResult(Output: (((m_history.CursorTick is { } at) && (m_history.HeadTick is { } top) && (at < top))
            ? $"[world.history: live input resumes at tick {at}; ticks {(at + 1UL)}..{top} are discarded when the next tick lands]"
            : $"[world.history: live input {(wasPaused ? "resumes" : "was already running")} at the head]"));
    }
    private CommandResult Seek(ulong target, string verb) {
        if (!m_history.TrySeek(
            documentPath: DocumentPath,
            refusal: out var refusal,
            report: out var report,
            target: target
        )) {
            return CommandResult.Error(output: $"[world.history: {verb} refused — {refusal}]");
        }

        var head = m_history.HeadTick!.Value;
        var paused = false;

        if (target < head) {
            paused = m_instances.TryPause(
                name: WorldInstanceHost.BootInstanceName,
                reason: out _
            );
        }

        var how = ((report.KeyframeTick is { } keyframe)
            ? $"restored keyframe {keyframe} ({(report.RebuiltDocument ? "through the load door" : "in place")}), re-simulated {report.TicksResimulated} tick(s)"
            : $"re-simulated {report.TicksResimulated} tick(s) forward");
        var proof = (report.Matches
            ? $"authoritative 0x{report.LiveHash:X16} matches the recording"
            : $"DIVERGED at tick {(report.DivergedAt ?? report.To)} — live 0x{report.LiveHash:X16}, recorded 0x{report.RecordedHash:X16}");
        var where = ((target < head)
            ? $" | {(paused ? "paused" : "held")} at tick {target} behind head {head} — world.history resume discards the future, world.history branch <name> keeps it"
            : " | at the head");

        return new CommandResult(Output: $"[world.history: {verb} {report.From} -> {report.To} | {how} | {proof}{where}]") { IsError = !report.Matches };
    }
    private CommandResult Status() {
        var status = m_history.Status();

        if (!status.On) {
            return new CommandResult(Output: "[world.history: off — world.history on [<MiB>] starts recording]");
        }

        var counters = status.Counters;
        var window = ((status.Oldest is { } oldest)
            ? $"window {oldest}..{status.Head} | cursor {status.Cursor} | {status.Keyframes} keyframe(s) every {status.Interval} tick(s)"
            : $"no window — {status.Waiting}");
        var meanKeyframe = ((counters.KeyframesCaptured > 0L)
            ? (counters.KeyframeBytesCaptured / counters.KeyframesCaptured)
            : 0L);
        var ticksHeld = (((status.Oldest is { } first) && (status.Head is { } last))
            ? ((long)(last - first))
            : 0L);
        var perTick = ((ticksHeld > 0L)
            ? (status.InputBytes / ticksHeld)
            : 0L);
        var branches = ((status.Branches.Count == 0)
            ? "none"
            : string.Join(
                separator: ", ",
                values: status.Branches.Select(selector: static branch => $"'{branch.Name}' {(branch.ForkTick + 1UL)}..{branch.HeadTick}")
            ));

        return new CommandResult(Output: $"[world.history: on | {window} | held {Bytes(bytes: status.BytesHeld)} of {Bytes(bytes: status.BudgetBytes)} (keyframes {Bytes(bytes: status.KeyframeBytes)}, input {Bytes(bytes: status.InputBytes)} = {Bytes(bytes: perTick)}/tick, branches {Bytes(bytes: status.BranchBytes)}) | recorded {counters.TicksRecorded} tick(s), {counters.HashFolds} hash fold(s), {counters.KeyframesCaptured} keyframe(s) captured ({Bytes(bytes: meanKeyframe)} mean), {counters.KeyframesDeferred} deferred, {counters.SegmentsEvicted} evicted | {counters.Seeks} seek(s), {counters.TicksResimulated} tick(s) re-simulated, {counters.InPlaceRestores} in-place and {counters.RebuildRestores} rebuilt restore(s) | {counters.BranchesKept} kept and {counters.FuturesDiscarded} discarded future(s) | branches: {branches}]");
    }
    private CommandResult Step(WireArgs args) {
        if (
            (args.Count != 2) ||
            !args.TryLong(index: 1, value: out var delta) ||
            (delta == 0L)
        ) {
            return CommandResult.Error(output: "[world.history: usage — world.history step <±n>, a nonzero tick count]");
        }

        if (m_history.CursorTick is not { } cursor) {
            return Seek(target: 0UL, verb: "step");
        }

        if (
            (delta < 0L) &&
            (((ulong)(-delta)) > cursor)
        ) {
            return CommandResult.Error(output: $"[world.history: step refused — {delta} from tick {cursor} is before tick 0]");
        }

        return Seek(
            target: ((delta < 0L)
                ? (cursor - ((ulong)(-delta)))
                : (cursor + ((ulong)delta))),
            verb: "step"
        );
    }
    private CommandResult Dispatch(WireArgs args) {
        if (args.Count == 0) {
            return Status();
        }

        if (args.Is(index: 0, value: "on")) {
            return On(args: args);
        }

        if (args.Is(index: 0, value: "off") && (args.Count == 1)) {
            m_history.Off();

            return new CommandResult(Output: "[world.history: off — everything it held is released]");
        }

        if (args.Is(index: 0, value: "status") && (args.Count == 1)) {
            return Status();
        }

        if (args.Is(index: 0, value: "seek")) {
            return (((args.Count == 2) && args.TryUnsignedDigits(index: 1, value: out var target))
                ? Seek(target: target, verb: "seek")
                : CommandResult.Error(output: "[world.history: usage — world.history seek <tick>]"));
        }

        if (args.Is(index: 0, value: "step")) {
            return Step(args: args);
        }

        if (args.Is(index: 0, value: "diff")) {
            return Diff(args: args);
        }

        if (args.Is(index: 0, value: "resume") && (args.Count == 1)) {
            return Resume();
        }

        if (args.Is(index: 0, value: "branch")) {
            return Branch(args: args);
        }

        if (args.Is(index: 0, value: "replay-edit")) {
            return ReplayEdit(args: args);
        }

        return CommandResult.Error(output: Usage);
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: "Deterministic time travel over the boot world (Immediate): world.history on [<MiB>] keeps a bounded ring of checkpoint keyframes plus each tick's recorded input and authoritative hash (default 64 MiB; off by default); off releases it; status (or no argument) echoes the window, the cursor, the keyframe spacing, the bytes held against the budget, and the counted cost. seek <tick> moves the live world to any tick in the window, backward or forward, by restoring the nearest keyframe and re-simulating the recorded input, proving every tick against the recorded hash; a seek behind the head pauses the world. step <±n> seeks relative to the cursor. resume continues live input from the cursor and discards the recorded future at the next tick; branch <name> keeps that future under the name instead. diff <a> <b> [--json] re-simulates both ticks in an isolated shadow and prints which bodies, cells, fields, and hash components changed, with values (--json prints the exact machine form). replay-edit <n> moves the document edits made at the cursor n ticks earlier in an isolated shadow and reports the first tick the world would have diverged, with the diff there. Refused by name across a crossing, a live neighbour, a remote peer, an engagement, an active replay recording or drive, input not yet run, and state a checkpoint cannot capture.",
            handler: (_, args) => Dispatch(args: args),
            name: "world.history"
        );
    }
}
