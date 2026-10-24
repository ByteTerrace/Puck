using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Console lines pinned to exact ticks of the boot world (<c>world.at</c>): each armed line runs in the console drain
/// right after the boot world completes its tick and before the next tick steps, the point a line released by
/// <c>world.wait</c> runs at, so an Immediate line (a read, a capture) sees the state of that tick and a Simulation line
/// (a body intent or pose, <c>view.override</c>) applies in the tick after it. Where the line came from does not move
/// it: a line armed over a control attachment runs on the same tick however long the attachment's round trips take, so
/// the same script gives the same ticks on every run.
/// </summary>
/// <remarks>
/// <para>Operator lines run under the console principal through one session of their own; test steps use their own
/// seat-bound sessions. Test steps submit in the authority tick-complete hook, before sibling instances advance
/// and test results export; operator lines submit in the following console drain. Both use the same tick queue
/// and command dispatch. Lines are submitted straight to the registry,
/// so the session's read-after-write barrier never holds a line behind another line of the same tick. Lines of each
/// ingress run in arming order. A tick a rewound authority timeline publishes again runs
/// nothing a second time, since a line is removed from the pending set as it runs.</para>
/// <para>A settled capture a line arms (<c>world.screenshot</c>) is held for like any other: the rendered host steps no
/// tick past the line's tick until a frame of that tick serves it.</para>
/// <para>Every member runs on the host pump.</para>
/// </remarks>
/// <param name="completedTick">Reads the boot world's last completed tick.</param>
/// <param name="source">Resolves the console text source the lines' session drains through.</param>
/// <param name="registry">Resolves the command registry the lines are submitted to.</param>
public sealed class WorldTickScript(Func<ulong> completedTick, Func<TextCommandSource> source, Func<CommandRegistry> registry) {
    /// <summary>One armed line and its exact submission result, shared by operator scripts and test runs.</summary>
    public sealed class Line {
        internal Line(ulong tick, string text) {
            Tick = tick;
            Text = text;
        }

        internal Func<InputRouter>? Router { get; init; }
        internal TextCommandSession? Session { get; set; }
        internal int Slot { get; init; }

        /// <summary>Gets the handler's answer, empty until the line answers.</summary>
        public string Answer { get; internal set; } = string.Empty;
        /// <summary>Gets unreached, pending, ran, refused or faulted.</summary>
        public string Outcome { get; internal set; } = "unreached";

        /// <summary>Gets the tick the line is submitted after.</summary>
        public ulong Tick { get; }
        /// <summary>Gets the submitted command text.</summary>
        public string Text { get; }
    }

    /// <summary>The most lines one World holds armed at once.</summary>
    public const int MaxPending = 4096;
    /// <summary>The ran lines the read-back keeps, newest last.</summary>
    public const int MaxLog = 256;

    private const string Pending = "pending";

    private readonly LinkedList<Line> m_log = [];
    // Pending lines by tick, each tick's lines in arming order.
    private readonly WorldTickSchedule<Line> m_pending = new();
    // Simulation-routed lines submitted and not yet applied, in submission order: their answer arrives when their tick
    // applies, through the session's settled callback.
    private readonly LinkedList<Line> m_settling = [];

    private TextCommandSession? m_session;

    /// <summary>Gets the boot world's last completed tick, the coordinate every armed line is pinned to.</summary>
    public ulong CompletedTick => completedTick();
    /// <summary>Gets the number of lines armed and not yet run.</summary>
    public int PendingCount => m_pending.PendingCount;

    /// <summary>Arms a test step through a seat's ordinary ingress, on the same scheduler as world.at. It submits
    /// at the authority tick-complete hook, before sibling instances advance.</summary>
    /// <param name="tick">The completed tick after which to submit.</param>
    /// <param name="text">The command line.</param>
    /// <param name="slot">The seat slot.</param>
    /// <param name="router">Resolves the live seat router after boot composition.</param>
    /// <param name="line">The step and its eventual answer.</param>
    /// <returns>A refusal, or null when armed.</returns>
    public string? ArmSeat(ulong tick, string text, int slot, Func<InputRouter> router, out Line line) {
        line = new Line(text: text, tick: tick) { Router = router, Slot = slot };
        if (RefusalOf(line: text, tick: tick) is { } refusal) {
            return refusal;
        }
        m_pending.Add(row: line, tick: tick);
        return null;
    }
    /// <summary>Arms one line for a tick.</summary>
    /// <param name="tick">The boot world's tick after which the line runs; later than <see cref="CompletedTick"/>.</param>
    /// <param name="line">The console line.</param>
    /// <returns>Why the line was refused, or <see langword="null"/> when it is armed.</returns>
    public string? Arm(ulong tick, string line) {
        if (RefusalOf(line: line, tick: tick) is { } refusal) {
            return refusal;
        }

        Add(tick: tick, line: line.Trim());

        return null;
    }
    /// <summary>Arms a whole script, all or nothing: every line is checked before any is armed.</summary>
    /// <param name="lines">Each line with its tick.</param>
    /// <returns>Why the script was refused, naming the first line refused, or <see langword="null"/> when every line
    /// is armed.</returns>
    public string? Arm(IReadOnlyList<(ulong Tick, string Line)> lines) {
        ArgumentNullException.ThrowIfNull(argument: lines);

        if ((PendingCount + lines.Count) > MaxPending) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{lines.Count} lines would exceed the {MaxPending} a World holds armed ({PendingCount} are)");
        }

        for (var index = 0; (index < lines.Count); index++) {
            if (RefusalOf(tick: lines[index].Tick, line: lines[index].Line) is { } refusal) {
                return string.Create(provider: CultureInfo.InvariantCulture, handler: $"step {index}: {refusal}");
            }
        }

        foreach (var (tick, line) in lines) {
            Add(tick: tick, line: line.Trim());
        }

        return null;
    }
    /// <summary>Drops every armed line that has not run.</summary>
    /// <returns>The number of lines dropped.</returns>
    public int Clear() => m_pending.Clear();
    /// <summary>Describes the armed lines and the lines that ran, oldest first, one record.</summary>
    /// <returns>The read-back.</returns>
    public string Describe() {
        var builder = new StringBuilder();

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[world.at: tick {CompletedTick} pending {PendingCount} ran {m_log.Count}]");

        foreach (var ran in m_log) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  tick {ran.Tick} {ran.Outcome}: {ran.Text}{((ran.Answer.Length > 0) ? $" -> {ran.Answer}" : string.Empty)}");
        }

        foreach (var (tick, line) in m_pending.Pending) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  tick {tick} pending: {line.Text}");
        }

        return builder.ToString();
    }
    /// <summary>The authority tick-complete hook: submits test steps and queues operator lines for the console
    /// drain before the next tick steps.</summary>
    /// <param name="tick">The boot world's just-completed tick.</param>
    public void PublishTick(ulong tick) {
        if (!m_pending.TryTake(rows: out var due, tick: tick)) {
            return;
        }

        // Test worlds pin their submission before this host step advances siblings and exports results. Preserve
        // that boundary while sharing world.at's queue and dispatcher; an extra drain would delay sibling input.
        var deferred = false;

        foreach (var line in due) {
            if (line.Router is not null) {
                Run(line: line);
            } else {
                deferred = true;
            }
        }
        if (!deferred) {
            return;
        }
        // One host operation for the tick's operator lines: it runs in the next drain, and submits each to the
        // registry so no line of the tick waits behind another's simulation submission.
        _ = Session().InvokeAsync(operation: () => {
            foreach (var line in due) {
                if (line.Router is null) {
                    Run(line: line);
                }
            }

            return true;
        });
    }

    private void Add(ulong tick, string line) {
        m_pending.Add(tick: tick, row: new Line(text: line, tick: tick));
    }
    private void Log(Line line) {
        _ = m_log.AddLast(value: line);

        while (m_log.Count > MaxLog) {
            m_log.RemoveFirst();
        }
    }
    // Records a ran line's answer and narrates it on stderr, beside the lines it caused.
    private static void Answer(Line line, in CommandResult result) {
        line.Answer = result.Output.ReplaceLineEndings(replacementText: " ");
        line.Outcome = (result.Faulted ? "faulted" : (result.IsError ? "refused" : "ran"));
        Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.at] tick {line.Tick} {line.Outcome}: {line.Text}{((line.Answer.Length > 0) ? $" -> {line.Answer}" : string.Empty)}"));
        line.Session?.Dispose();
    }
    private void OnSettled(string text, CommandResult result) {
        if (
            (m_settling.First is { } first) &&
            string.Equals(a: first.Value.Text, b: text, comparisonType: StringComparison.Ordinal)
        ) {
            m_settling.RemoveFirst();
            Answer(line: first.Value, result: result);
        }
    }
    private string? RefusalOf(ulong tick, string line) {
        if (string.IsNullOrWhiteSpace(value: line)) {
            return "an empty line";
        }

        var verb = line.AsSpan().TrimStart();
        var end = verb.IndexOfAny(values: " \t");

        verb = ((end < 0) ? verb : verb[..end]);

        if (
            verb.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "world.at") ||
            verb.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "world.wait") ||
            verb.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "quit")
        ) {
            return $"'{verb}' cannot be pinned to a tick: a pinned line runs on its own tick, so it neither waits nor pins another, and quit ends the run that runs it";
        }

        if (tick <= CompletedTick) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"tick {tick} has already completed (the world is at tick {CompletedTick}); pin a later tick, or pause the world (world.rate pause) before arming a script for a near one");
        }

        return ((PendingCount >= MaxPending)
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"a World holds at most {MaxPending} armed lines")
            : null);
    }
    private void Run(Line line) {
        var commands = registry();
        var session = ((line.Router is { } router)
            ? line.Session = source().CreateSeatSession(router: router(), slot: line.Slot, dueNextTick: true,
                onSettled: (_, result) => Answer(line: line, result: result))
            : Session());
        var simulation = commands.RoutesToSimulation(line: line.Text, slot: session.Slot);

        line.Outcome = Pending;
        Log(line: line);

        var settling = ((simulation && (line.Router is null)) ? m_settling.AddLast(value: line) : null);
        var result = commands.SubmitSession(line: line.Text, session: session);

        // A simulation line answers when its tick applies; one refused at submission answers now.
        if (!simulation || result.IsError) {
            if (settling?.List is not null) {
                m_settling.Remove(node: settling);
            }

            Answer(line: line, result: result);
        }
    }
    private TextCommandSession Session() => (m_session ??= source().CreateSession(
        dueNextTick: true,
        onSettled: OnSettled,
        principal: Principal.Console
    ));
}
