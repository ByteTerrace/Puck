using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Launcher;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// <c>world.patrol</c>: walks body 0 across a grid and reports, per point, whether the seat's third-person view draws the
/// body and whether the host kept its frame budget. It arms <c>world.at</c> lines, so every pose, pick request and read
/// lands on an exact tick: a pose, a settle, then a pick of the centre of the seat's rendered view and, where the centre
/// does not name the body, <see cref="Samples"/> less one more at pixels about it, each read <see cref="GapTicks"/> ticks after it is asked. The patrol is
/// presentation: it poses through <c>body.pose</c> and reads the view's picker, changing no state beyond the pose.
/// </summary>
/// <remarks>Every member runs on the host pump. A stall is a host iteration whose work, its interval less the pacer's
/// wait (<see cref="HostFrameTimeline.Scan"/>), passed <see cref="BudgetMilliseconds"/>; a headless host records none.</remarks>
/// <param name="script">The pinned-line scheduler the patrol arms.</param>
/// <param name="view">Resolves the first seat's rendered view, or <see langword="null"/> without one.</param>
/// <param name="timeline">The windowed host's frame timeline, or <see langword="null"/>.</param>
public sealed partial class WorldPatrolCommandModule(WorldTickScript script, Func<IWorldPatrolView?> view, HostFrameTimeline? timeline = null) : ICommandModule {
    /// <summary>The host work, in milliseconds, past which an iteration is a stall: one 60 Hz period.</summary>
    public const double BudgetMilliseconds = 16.7;
    /// <summary>The most points one patrol visits, which keeps its lines within what <c>world.at</c> holds armed.</summary>
    public const int MaxPoints = 200;
    /// <summary>The pixels a point picks: the view's centre, then a ring about it.</summary>
    public const int Samples = 6;
    /// <summary>The ticks between a sample's request and the read of its answer.</summary>
    public const int GapTicks = 3;

    private const string Form = "<x0> <z0> <x1> <z1> <step> [yawDegrees|-] [settleTicks] [y|-], world.patrol all, world.patrol stop, or world.patrol";
    private const int DefaultSettle = 15;

    // The sampled pixels as offsets from the view's centre in normalized coordinates: the centre, then its head, each foot
    // and each shoulder, which a body of the Moth's size under the seat's chase framing spans while an exhibit in front of
    // its middle covers only part of them.
    private static readonly (float X, float Y)[] Offsets = [(0f, 0f), (0f, -0.2f), (-0.04f, 0.18f), (0.04f, 0.18f), (-0.08f, -0.12f), (0.08f, -0.12f)];

    private sealed class Point(float x, float z) {
        public float X { get; } = x;
        public float Z { get; } = z;
        public long[] Requests { get; } = new long[Samples];
        public SdfPickResult?[] Answers { get; } = new SdfPickResult?[Samples];
        public bool Read { get; set; }
        public (int Iterations, int Over, double WorstWork, double WorstInterval) Frames { get; set; }
    }

    private readonly List<Point> m_points = [];
    private ulong m_lastTick;
    private long m_mark;
    private string m_instance = string.Empty;
    private string m_yaw = "-";

    private bool Running => ((m_points.Count > 0) && !m_points[^1].Read && (script.PendingCount > 0));

    private static string F(float value) => value.ToString(provider: CultureInfo.InvariantCulture, format: "0.##");
    private IWorldPatrolView? PickerOf() {
        var picker = view();

        m_instance = (picker?.Instance ?? m_instance);

        return picker;
    }
    private CommandResult Arm(float x0, float z0, float x1, float z1, float step, string yaw, int settle, string height) {
        if (PickerOf() is null) {
            return CommandResult.Error(output: "[world.patrol: needs a rendered seat view, so a windowed or offscreen World]");
        }
        if (Running) {
            return CommandResult.Error(output: $"[world.patrol: a patrol runs to tick {m_lastTick}; world.patrol stop ends it]");
        }

        var columns = (1 + ((int)MathF.Floor(x: ((MathF.Abs(x: (x1 - x0)) / step) + 1e-4f))));
        var rows = (1 + ((int)MathF.Floor(x: ((MathF.Abs(x: (z1 - z0)) / step) + 1e-4f))));

        if (((long)columns * rows) > MaxPoints) {
            return CommandResult.Error(output: $"[world.patrol: {columns}x{rows} points exceed the {MaxPoints} one patrol visits; widen the step or patrol in parts]");
        }

        m_points.Clear();
        for (var row = 0; (row < rows); row++) {
            for (var column = 0; (column < columns); column++) {
                var across = (((row % 2) == 0) ? column : ((columns - 1) - column));

                m_points.Add(item: new Point(x: (x0 + (across * ((x1 >= x0) ? step : -step))), z: (z0 + (row * ((z1 >= z0) ? step : -step)))));
            }
        }

        m_yaw = yaw;
        m_mark = (timeline?.Recorded ?? 0L);

        var start = (script.CompletedTick + 2UL);
        var stride = ((ulong)(settle + (Samples * GapTicks) + 1));
        var lines = new List<(ulong Tick, string Line)>(capacity: (m_points.Count * (Samples * 2)));

        for (var index = 0; (index < m_points.Count); index++) {
            var tick = (start + (stride * ((ulong)index)));
            var first = (tick + ((ulong)settle));

            lines.Add(item: (tick, $"body.pose {F(m_points[index].X)} {height} {F(m_points[index].Z)} {yaw} - -"));
            // A tick runs its lines in arming order: the previous sample is read before the next is asked.
            for (var sample = 0; (sample < Samples); sample++) {
                var at = (first + ((ulong)(sample * GapTicks)));

                if (sample > 0) { lines.Add(item: (at, $"world.patrol read {index} {sample - 1}")); }
                lines.Add(item: (at, $"world.patrol ask {index} {sample}"));
            }
            lines.Add(item: ((first + ((ulong)(Samples * GapTicks))), $"world.patrol read {index} {Samples - 1}"));
        }
        m_lastTick = lines[^1].Tick;

        if (script.Arm(lines: lines) is { } refusal) {
            m_points.Clear();

            return CommandResult.Error(output: $"[world.patrol: refused, nothing armed — {refusal}]");
        }

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.patrol: armed {m_points.Count} points ({columns}x{rows}), settle {settle} ticks and {Samples} picks each, through tick {m_lastTick} (now tick {script.CompletedTick}); world.wait until {m_lastTick}, then world.patrol reads the result]"));
    }
    private CommandResult Ask(int index, int sample) {
        if ((index < 0) || (index >= m_points.Count) || (sample < 0) || (sample >= Samples) || (PickerOf() is not { } picker)) {
            return CommandResult.Error(output: "[world.patrol: ask names no armed sample]");
        }

        // The ring is only asked where the centre does not name the body, which keeps a clear point's cost to one pick.
        if ((sample > 0) && IsBody(answer: m_points[index].Answers[0])) {
            m_points[index].Requests[sample] = -1L;

            return new CommandResult(Output: $"[world.patrol: point {index} sample {sample} skipped]");
        }

        m_points[index].Requests[sample] = picker.Request(x: (0.5f + Offsets[sample].X), y: (0.5f + Offsets[sample].Y));

        return new CommandResult(Output: $"[world.patrol: point {index} sample {sample} asked request={m_points[index].Requests[sample]}]");
    }
    private CommandResult Read(int index, int sample) {
        if ((index < 0) || (index >= m_points.Count) || (sample < 0) || (sample >= Samples) || (PickerOf() is not { } picker)) {
            return CommandResult.Error(output: "[world.patrol: read names no armed sample]");
        }

        var point = m_points[index];

        if (point.Requests[sample] >= 0L) {
            point.Answers[sample] = ((picker.Result is { } answer) && (answer.Request == point.Requests[sample]) ? answer : null);
        }
        if (sample == (Samples - 1)) {
            var now = (timeline?.Recorded ?? 0L);

            point.Read = true;
            point.Frames = (timeline?.Scan(from: m_mark, to: now, budgetMilliseconds: BudgetMilliseconds) ?? default);
            m_mark = now;

            return new CommandResult(Output: $"[world.patrol: point {index} {Verdict(point: point)}]");
        }

        return new CommandResult(Output: $"[world.patrol: point {index} sample {sample} read]");
    }
    private static bool IsBody(SdfPickResult? answer) => (answer?.Target is WorldPickTarget { BodyIndex: 0 });
    private static int BodySamples(Point point) => point.Answers.Count(predicate: static answer => IsBody(answer: answer));
    private static bool Flagged(Point point) => (point.Read && (BodySamples(point: point) == 0));
    private static string Named(SdfPickResult? answer) {
        if (answer is not { } picked) { return "no answer"; }
        if (!picked.Hit) { return "sky"; }

        var target = (picked.Target as WorldPickTarget);

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"kind={((uint)picked.Kind)} source={picked.Source} placement={(target?.Placement ?? "none")} body={(target?.BodyIndex?.ToString(provider: CultureInfo.InvariantCulture) ?? "none")}");
    }
    private static string Verdict(Point point) {
        if (!point.Read) { return "unread"; }

        var frames = ((point.Frames.Over > 0)
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $" STALL {point.Frames.Over}/{point.Frames.Iterations} frames, worst work {point.Frames.WorstWork:0.0} ms")
            : string.Empty);
        var body = BodySamples(point: point);
        var seen = (IsBody(answer: point.Answers[0]) ? "centre on body" : $"body at {body}/{Samples} picks");

        if (body == 0) {
            return (point.Answers.All(predicate: static answer => (answer is null))
                ? $"NO ANSWER (no pick completed){frames}"
                : $"OFF BODY ({seen}; centre drew {Named(answer: point.Answers[0])}){frames}");
        }

        return (IsBody(answer: point.Answers[0])
            ? $"on screen ({seen}){frames}"
            : $"OCCLUDED ({seen}; centre drew {Named(answer: point.Answers[0])}){frames}");
    }
    private CommandResult Describe(bool all) {
        if (m_points.Count == 0) {
            return new CommandResult(Output: "[world.patrol: none armed]");
        }

        var builder = new StringBuilder();
        var read = m_points.Count(predicate: static point => point.Read);
        var off = m_points.Count(predicate: static point => Flagged(point: point));
        var occluded = m_points.Count(predicate: static point => (point.Read && !Flagged(point: point) && !IsBody(answer: point.Answers[0])));
        var stalled = m_points.Count(predicate: static point => (point.Frames.Over > 0));
        var worst = m_points.Max(selector: static point => point.Frames.WorstWork);
        var pending = (m_points.Count - read);

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[world.patrol: instance={m_instance} points={m_points.Count} read={read}{((pending > 0) ? $" (running to tick {m_lastTick}, now {script.CompletedTick})" : string.Empty)} off-body={off} occluded={occluded} stalled-points={stalled} worst-work={worst:0.0} ms budget={BudgetMilliseconds} ms{(all ? string.Empty : "; flagged points only, world.patrol all lists every point")}]");
        for (var index = 0; (index < m_points.Count); index++) {
            var point = m_points[index];

            if (all || Flagged(point: point) || (point.Read && (!IsBody(answer: point.Answers[0]) || (point.Frames.Over > 0)))) {
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  #{index} ({F(point.X)}, {F(point.Z)}) yaw={m_yaw}: {Verdict(point: point)}");
            }
        }

        return new CommandResult(Output: builder.ToString());
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: "world.patrol",
            description: $"Walks body 0 across a grid of its current level and reports, per point, whether the seat's rendered third-person view draws the body and whether the host kept its frame budget: world.patrol <x0> <z0> <x1> <z1> <step> [yawDegrees|-] [settleTicks] [y|-] arms one world.at script and answers with the tick it ends at. Each point is a body.pose of body 0 to (x, y, z), y held at the body's own by default (a grounded body re-pins to the floor under it; name y to patrol another level), a settle of settleTicks ticks (default {DefaultSettle}), then a pick of the centre of the first seat's rendered view (world.view.pick primary 0.5 0.5) and, only where the centre does not name the body, {Samples - 1} more at pixels about it, each read {GapTicks} ticks after it is asked (a clear point costs one pick); rows alternate direction, at most {MaxPoints} points. world.wait until <tick> holds a session until the patrol ends, and world.patrol alone reads it back: a summary line (points read, points off the body, points the centre pick shows an occluder in front of the body, points whose frames stalled, the worst host work in one iteration against {BudgetMilliseconds} ms) and a line for each point that is not plainly on screen or that stalled; world.patrol all lists every point. A point reads OFF BODY when no pick names body:0, the failure (the centre's pick says what is drawn there, sky included); OCCLUDED when the centre names something else but another pick names the body (an exhibit between the camera and the body); NO ANSWER when no pick completed. A stall is a host iteration whose work (its interval less the pacer's wait) passed {BudgetMilliseconds} ms during the point; a World without a windowed host records none. world.patrol stop drops every armed line, the patrol's and any other world.at line, and keeps what was read. world.patrol ask <point> <sample> and world.patrol read <point> <sample> are the script's own lines. Refused without a rendered seat view, while a patrol runs, past {MaxPoints} points, or when the script would exceed what world.at holds armed.",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return Describe(all: false);
                }
                if ((args.Count == 1) && args.Is(index: 0, value: "all")) {
                    return Describe(all: true);
                }
                if ((args.Count == 1) && args.Is(index: 0, value: "stop")) {
                    return new CommandResult(Output: $"[world.patrol: stopped, dropped {script.Clear()} armed line(s)]");
                }
                if ((args.Count == 3) && (args.Is(index: 0, value: "ask") || args.Is(index: 0, value: "read")) && args.TryInt(index: 1, value: out var point) && args.TryInt(index: 2, value: out var sample)) {
                    return (args.Is(index: 0, value: "ask") ? Ask(index: point, sample: sample) : Read(index: point, sample: sample));
                }

                var settle = DefaultSettle;
                var yaw = "-";
                var step = 0f;

                if (
                    (args.Count is < 5 or > 8) ||
                    ((args.Count > 7) && !args.Is(index: 7, value: "-") && !args.TryFloat(index: 7, value: out var level)) ||
                    !args.TryFloat(index: 0, value: out var x0) ||
                    !args.TryFloat(index: 1, value: out var z0) ||
                    !args.TryFloat(index: 2, value: out var x1) ||
                    !args.TryFloat(index: 3, value: out var z1) ||
                    !args.TryFloat(index: 4, value: out step) ||
                    !float.IsFinite(f: step) || (step <= 0f) ||
                    ((args.Count > 5) && !args.Is(index: 5, value: "-") && !args.TryFloat(index: 5, value: out var heading)) ||
                    ((args.Count > 6) && (!args.TryInt(index: 6, value: out settle) || (settle < 1)))
                ) {
                    return CommandResult.Usage(form: Form, verb: "world.patrol");
                }
                if (args.Count > 5) { yaw = args[5].ToString(); }

                return Arm(height: (args.Count > 7 ? args[7].ToString() : "-"), settle: settle, step: step, x0: x0, x1: x1, yaw: yaw, z0: z0, z1: z1);
            }
        );
    }
}
