using System.Globalization;
using System.Text;
using Puck.Commands;

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
/// <para>Lines run under the console principal through one session of their own, submitted straight to the registry,
/// so the session's read-after-write barrier never holds a line behind another line of the same tick: every line of a
/// tick runs in the drain after it, in the order it was armed. A tick a rewound authority timeline publishes again runs
/// nothing a second time, since a line is removed from the pending set as it runs.</para>
/// <para>A settled capture a line arms (<c>world.screenshot</c>) is held for like any other: the rendered host steps no
/// tick past the line's tick until a frame of that tick serves it.</para>
/// <para>Every member runs on the host pump.</para>
/// </remarks>
/// <param name="completedTick">Reads the boot world's last completed tick.</param>
/// <param name="source">Resolves the console text source the lines' session drains through.</param>
/// <param name="registry">Resolves the command registry the lines are submitted to.</param>
public sealed class WorldTickScript(Func<ulong> completedTick, Func<TextCommandSource> source, Func<CommandRegistry> registry) {
    private sealed class Line(ulong tick, string text) {
        public string Answer = string.Empty;
        public string Outcome = Pending;
        public ulong Tick { get; } = tick;
        public string Text { get; } = text;
    }

    /// <summary>The most lines one World holds armed at once.</summary>
    public const int MaxPending = 4096;
    /// <summary>The ran lines the read-back keeps, newest last.</summary>
    public const int MaxLog = 256;

    private const string Pending = "pending";

    private readonly LinkedList<Line> m_log = [];
    // Pending lines by tick, each tick's lines in arming order.
    private readonly SortedDictionary<ulong, List<Line>> m_pending = [];
    // Simulation-routed lines submitted and not yet applied, in submission order: their answer arrives when their tick
    // applies, through the session's settled callback.
    private readonly LinkedList<Line> m_settling = [];

    private int m_pendingCount;
    private TextCommandSession? m_session;

    /// <summary>Gets the boot world's last completed tick, the coordinate every armed line is pinned to.</summary>
    public ulong CompletedTick => completedTick();
    /// <summary>Gets the number of lines armed and not yet run.</summary>
    public int PendingCount => m_pendingCount;

    /// <summary>Arms one line for a tick.</summary>
    /// <param name="tick">The boot world's tick after which the line runs; later than <see cref="CompletedTick"/>.</param>
    /// <param name="line">The console line.</param>
    /// <returns>Why the line was refused, or <see langword="null"/> when it is armed.</returns>
    public string? Arm(ulong tick, string line) {
        if (RefusalOf(tick: tick, line: line) is { } refusal) {
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

        if ((m_pendingCount + lines.Count) > MaxPending) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{lines.Count} lines would exceed the {MaxPending} a World holds armed ({m_pendingCount} are)");
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
    public int Clear() {
        var dropped = m_pendingCount;

        m_pending.Clear();
        m_pendingCount = 0;

        return dropped;
    }
    /// <summary>Describes the armed lines and the lines that ran, oldest first, one record.</summary>
    /// <returns>The read-back.</returns>
    public string Describe() {
        var builder = new StringBuilder();

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[world.at: tick {CompletedTick} pending {m_pendingCount} ran {m_log.Count}]");

        foreach (var ran in m_log) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  tick {ran.Tick} {ran.Outcome}: {ran.Text}{((ran.Answer.Length > 0) ? $" -> {ran.Answer}" : string.Empty)}");
        }

        foreach (var (tick, lines) in m_pending) {
            foreach (var line in lines) {
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  tick {tick} pending: {line.Text}");
            }
        }

        return builder.ToString();
    }
    /// <summary>The authority tick-complete hook: queues the lines armed for <paramref name="tick"/> to run in the
    /// console drain before the next tick steps.</summary>
    /// <param name="tick">The boot world's just-completed tick.</param>
    public void PublishTick(ulong tick) {
        if (!m_pending.Remove(key: tick, value: out var due)) {
            return;
        }

        m_pendingCount -= due.Count;
        // One host operation for the tick's lines: it runs in the next drain, and submits every line straight to the
        // registry so no line of the tick waits behind another's simulation submission.
        _ = Session().InvokeAsync(operation: () => {
            foreach (var line in due) {
                Run(line: line);
            }

            return true;
        });
    }

    private void Add(ulong tick, string line) {
        if (!m_pending.TryGetValue(key: tick, value: out var atTick)) {
            atTick = [];
            m_pending[tick] = atTick;
        }

        atTick.Add(item: new Line(text: line, tick: tick));
        m_pendingCount++;
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
        line.Outcome = (result.IsError ? "refused" : "ran");
        Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.at] tick {line.Tick} {line.Outcome}: {line.Text}{((line.Answer.Length > 0) ? $" -> {line.Answer}" : string.Empty)}"));
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
            verb.Equals(other: "world.at", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            verb.Equals(other: "world.wait", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            verb.Equals(other: "quit", comparisonType: StringComparison.OrdinalIgnoreCase)
        ) {
            return $"'{verb}' cannot be pinned to a tick: a pinned line runs on its own tick, so it neither waits nor pins another, and quit ends the run that runs it";
        }

        if (tick <= CompletedTick) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"tick {tick} has already completed (the world is at tick {CompletedTick}); pin a later tick, or pause the world (world.rate pause) before arming a script for a near one");
        }

        return (m_pendingCount >= MaxPending)
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"a World holds at most {MaxPending} armed lines")
            : null;
    }
    private void Run(Line line) {
        var commands = registry();
        var session = Session();
        var simulation = commands.RoutesToSimulation(line: line.Text, slot: session.Slot);

        Log(line: line);

        var settling = (simulation ? m_settling.AddLast(value: line) : null);
        var result = commands.SubmitSession(line: line.Text, session: session);

        // A simulation line answers when its tick applies; one refused at submission answers now.
        if ((settling is null) || result.IsError) {
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
