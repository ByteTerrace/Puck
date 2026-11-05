using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Puck.Cli.Sweep;

/// <summary>What a sweep runs: the tools it drives for a bounded span, the signals it reads, and the capture tour
/// (<c>worlds/debug/sweep.json</c>). Every line is an existing console verb or an authored <c>commands</c> row.</summary>
/// <param name="Tools">The tools, run in order before the signals on the headless leg.</param>
/// <param name="Signals">The signals, read in order on every leg they name.</param>
/// <param name="Tour">The capture tour, run on the windowed leg.</param>
/// <param name="Note">The plan's explanatory note.</param>
/// <param name="Scenarios">Test source paths or file patterns, run serially by <c>puck test</c>.</param>
public sealed record SweepPlan(IReadOnlyList<SweepTool> Tools, IReadOnlyList<SweepSignal> Signals, SweepTour Tour, string? Note = null, IReadOnlyList<string>? Scenarios = null) {
    private static readonly JsonSerializerOptions Options = new() {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Parses a plan file's text.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="error">Why the text is not a plan.</param>
    /// <returns><see langword="true"/> when the text is a plan.</returns>
    public static bool TryParse(string json, out SweepPlan plan, out string error) {
        plan = new SweepPlan(Signals: [], Tools: [], Tour: SweepTour.Empty);

        try {
            var parsed = JsonSerializer.Deserialize<SweepPlan>(json: json, options: Options);

            if (parsed is null) {
                error = "the file holds no plan";

                return false;
            }

            plan = parsed with {
                Signals = (parsed.Signals ?? []),
                Tools = (parsed.Tools ?? []),
                Tour = (parsed.Tour ?? SweepTour.Empty),
            };
            plan = plan with { Tour = plan.Tour with { Stops = (plan.Tour.Stops ?? []) } };
        } catch (JsonException exception) {
            error = exception.Message;

            return false;
        }

        if ((plan.Tools.Count == 0) && (plan.Signals.Count == 0) && (plan.Tour.Stops.Count == 0)) {
            error = "the plan measures nothing: name a tool, signal or tour stop";
            return false;
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var tool in plan.Tools) {
            if (string.IsNullOrWhiteSpace(value: tool.Name) || (tool.Lines is null) || ((tool.Lines.Count == 0) && (tool.Until is null)) || (tool.EveryTicks <= 0) || (tool.MaxTicks < 0) ||
                ((tool.Until is { } until) && (string.IsNullOrWhiteSpace(value: until.Line) || string.IsNullOrWhiteSpace(value: until.Metric) || (until.In is not { Count: > 0 })))) {
                error = "every tool needs a name, lines, a positive poll interval and a bounded, nonempty poll when present";
                return false;
            }
        }

        foreach (var signal in plan.Signals) {
            if (signal.TimeoutSeconds is <= 0 or > 120) {
                error = $"signal '{signal.Name}' needs a timeout in 1..120 seconds, the attachment's call limit; split longer waits";
                return false;
            }
            if (string.IsNullOrWhiteSpace(value: signal.Name) || string.IsNullOrWhiteSpace(value: signal.Line)) {
                error = "every signal names itself and the line it reads";

                return false;
            }
            if (!names.Add(item: signal.Name)) {
                error = $"two signals are named '{signal.Name}'";

                return false;
            }
            if (!SweepSignals.Parsers.Contains(value: signal.Parse)) {
                error = $"signal '{signal.Name}' parses as '{signal.Parse}', which is not one of {string.Join(separator: ", ", values: SweepSignals.Parsers)}";

                return false;
            }
            if (signal.Leg is not (SweepSignal.AnyLeg or SweepLegs.Headless or SweepLegs.Windowed)) {
                error = $"signal '{signal.Name}' names the leg '{signal.Leg}' (any, headless or windowed)";

                return false;
            }

            if ((signal.MaxAsks is < 1 or > 64) || ((signal.MaxAsks > 1) && (signal.Pending is null))) {
                error = $"signal '{signal.Name}' asks its line 1..64 times, and more than once only with a pending rule that says when to ask again";

                return false;
            }

            foreach (var rule in signal.RedRules.Concat(second: signal.AmberRules).Concat(second: (signal.Pending is null) ? [] : [signal.Pending])) {
                if (!SweepRule.TryParse(rule: out _, text: rule)) {
                    error = $"signal '{signal.Name}' has the rule '{rule}', which is not '<metric> <op> <value>' with op one of {string.Join(separator: " ", values: SweepRule.Operators)}";

                    return false;
                }
            }
        }

        names.Clear();

        foreach (var stop in plan.Tour.Stops) {
            if (stop.ExpectedMotion is { } pattern) {
                try {
                    _ = new Regex(pattern: pattern, options: RegexOptions.CultureInvariant, matchTimeout: TimeSpan.FromSeconds(value: 2));
                } catch (ArgumentException exception) {
                    error = $"the tour stop '{stop.Name}' has an invalid expectedMotion: {exception.Message}";
                    return false;
                }
            }
            if (string.IsNullOrWhiteSpace(value: stop.Name) || (stop.Name.IndexOfAny(anyOf: Path.GetInvalidFileNameChars()) >= 0)) {
                error = $"the tour stop '{stop.Name}' needs a name that is a file name";

                return false;
            }
            if (!names.Add(item: stop.Name)) {
                error = $"two tour stops are named '{stop.Name}'";

                return false;
            }

            foreach (var step in (stop.Steps ?? [])) {
                if ((step.At < 0) || string.IsNullOrWhiteSpace(value: step.Run) || step.Run.TrimStart().StartsWith(comparisonType: StringComparison.Ordinal, value: "world.wait")) {
                    error = $"the tour stop '{stop.Name}' has a step that is not a console line at a tick offset of zero or more (world.wait is not pinnable)";

                    return false;
                }
                if (step.At > (stop.Settle ?? plan.Tour.Settle)) {
                    error = $"the tour stop '{stop.Name}' pins a step after its capture";

                    return false;
                }
            }
        }

        error = string.Empty;

        return true;
    }
}
/// <summary>The legs a sweep runs: a headless World of its own, and a windowed one for the tour.</summary>
public static class SweepLegs {
    /// <summary>The headless leg.</summary>
    public const string Headless = "headless";
    /// <summary>The windowed leg.</summary>
    public const string Windowed = "windowed";
}
/// <summary>A tool driven for a bounded span: lines that start it, then a read polled until it reports done.</summary>
/// <param name="Name">The tool's name in the report.</param>
/// <param name="Note">What the tool exercises.</param>
/// <param name="Lines">The lines that start it.</param>
/// <param name="Until">The read that ends it, or <see langword="null"/> to end after the lines.</param>
/// <param name="EveryTicks">The ticks between polls.</param>
/// <param name="MaxTicks">The most ticks the tool may run before the sweep records it as unfinished.</param>
public sealed record SweepTool(string Name, string? Note, IReadOnlyList<string> Lines, SweepUntil? Until, int EveryTicks = 60, int MaxTicks = 1200);
/// <summary>The read that ends a tool: it is done when the metric's value is one of the listed values.</summary>
/// <param name="Line">The read.</param>
/// <param name="Metric">The metric, as the <c>cells</c> or <c>words</c> parser names it.</param>
/// <param name="In">The values that mean done.</param>
/// <param name="Red">The values among them that mean done and red, or <see langword="null"/> for none.</param>
public sealed record SweepUntil(string Line, string Metric, IReadOnlyList<string> In, IReadOnlyList<string>? Red = null);
/// <summary>One signal: a read, how its output becomes metrics, and the rules that colour it.</summary>
/// <param name="Name">The signal's name in the report.</param>
/// <param name="Line">The console line, or several separated by newlines whose last one answers.</param>
/// <param name="Parse">How the answer becomes metrics (<see cref="SweepSignals.Parsers"/>).</param>
/// <param name="Leg">The legs that read it: <c>any</c>, <c>headless</c> or <c>windowed</c>.</param>
/// <param name="Red">Rules any one of which makes the signal red.</param>
/// <param name="Amber">Rules any one of which makes the signal amber.</param>
/// <param name="Compare">How each metric is judged against a previous report: <c>exact</c>,
/// <c>relative:&lt;fraction&gt;</c> or <c>none</c>, by metric name, a name ending in <c>*</c> covering a prefix.</param>
/// <param name="Metrics">Extra metrics read from the answer by a regular expression's first group.</param>
/// <param name="TimeoutSeconds">How long the read may take.</param>
/// <param name="Recheck">Whether the signal is a failure signal that is read again, as <c>&lt;name&gt;.after-tour</c>,
/// once the tour has run, so a violation the walking workload or the tour caused is not missed.</param>
/// <param name="Pending">A rule that holds while the World's answer says the read is unfinished and a later ask resumes
/// it, for a line whose work outlasts one call's deadline; <see langword="null"/> for a line that answers whole.</param>
/// <param name="MaxAsks">The most times the line is asked in all while <paramref name="Pending"/> holds, each ask with
/// the signal's own deadline; a read still pending after the last is unmeasured.</param>
public sealed record SweepSignal(
    string Name,
    string Line,
    string Parse = "text",
    string Leg = SweepSignal.AnyLeg,
    IReadOnlyList<string>? Red = null,
    IReadOnlyList<string>? Amber = null,
    IReadOnlyDictionary<string, string>? Compare = null,
    IReadOnlyDictionary<string, string>? Metrics = null,
    int TimeoutSeconds = 60,
    bool Recheck = false,
    string? Pending = null,
    int MaxAsks = 1
) {
    /// <summary>The leg value that reads a signal on every leg.</summary>
    public const string AnyLeg = "any";

    /// <summary>Gets the red rules, empty when the plan names none.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> RedRules => (Red ?? []);
    /// <summary>Gets the amber rules, empty when the plan names none.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AmberRules => (Amber ?? []);

    /// <summary>Returns whether the signal is read on a leg.</summary>
    /// <param name="leg">The leg.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool ReadsOn(string leg) => ((Leg == AnyLeg) || (Leg == leg));
}
/// <summary>The capture tour.</summary>
/// <param name="Prelude">Lines run once before the first stop.</param>
/// <param name="Reset">Lines run before every stop.</param>
/// <param name="Frame">Lines pinned to every stop's start tick after its bakes settle: camera framing and animation reseeding.</param>
/// <param name="Settle">The ticks a stop waits before its capture unless it names its own.</param>
/// <param name="LevelTolerance">The channel difference beyond which a pixel counts as changed against the baseline.</param>
/// <param name="AmberFraction">The changed-pixel fraction beyond which a still is amber against the baseline.</param>
/// <param name="Columns">The stills in one row of a contact sheet.</param>
/// <param name="CellWidth">The width a still is scaled to on a contact sheet.</param>
/// <param name="Stops">The stops, in order.</param>
/// <param name="Note">The tour's explanatory note.</param>
public sealed record SweepTour(IReadOnlyList<string>? Prelude, IReadOnlyList<string>? Reset, IReadOnlyList<string>? Frame, int Settle, int LevelTolerance, double AmberFraction, int Columns, int CellWidth, IReadOnlyList<SweepStop> Stops, string? Note = null) {
    /// <summary>A tour with no stops.</summary>
    public static SweepTour Empty { get; } = new(AmberFraction: 0.02, CellWidth: 480, Columns: 4, Frame: [], LevelTolerance: 16, Prelude: [], Reset: [], Settle: 45, Stops: []);
}
/// <summary>One stop of the tour: lines that pose the body and camera, then a settled capture.</summary>
/// <param name="Name">The still's name and file name.</param>
/// <param name="Group">The contact sheet the still belongs to.</param>
/// <param name="Note">What the still shows.</param>
/// <param name="Lines">The lines that pose it.</param>
/// <param name="Settle">The ticks from the start tick to the capture, or <see langword="null"/> for the tour's.</param>
/// <param name="After">Lines run after the capture, to undo a form the stop entered.</param>
/// <param name="Steps">Lines pinned to ticks after the start tick: the motion a still shows. The start tick is a fixed
/// lead after the tick at which the stop's own lines and the bakes have settled, so the same stop captures the same
/// motion tick whatever the bakes cost.</param>
/// <param name="ExpectedMotion">An optional regular expression the held pose and rig must match.</param>
public sealed record SweepStop(string Name, string? Group, string? Note, IReadOnlyList<string> Lines, int? Settle = null, IReadOnlyList<string>? After = null, IReadOnlyList<SweepStep>? Steps = null, string? ExpectedMotion = null);
/// <summary>One console line pinned to a tick of a stop.</summary>
/// <param name="At">The ticks after the stop's start tick.</param>
/// <param name="Run">The console line; never a <c>world.wait</c>.</param>
public sealed record SweepStep(int At, string Run);
