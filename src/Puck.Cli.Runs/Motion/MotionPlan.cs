using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Puck.Cli.Motion;

/// <summary>One settled still a scenario takes: the tick it is pinned to and where its files go.</summary>
/// <param name="Offset">The capture tick, counted from the scenario's tick 0.</param>
/// <param name="Tick">The absolute World tick the still is pinned to.</param>
/// <param name="Stem">The path of the still's composed frame without its <c>.png</c>; each camera's slot is <c>&lt;stem&gt;-&lt;camera&gt;.png</c>. Forward slashes.</param>
public sealed record MotionStill(int Offset, ulong Tick, string Stem);
/// <summary>One scenario laid out on the World's absolute ticks.</summary>
/// <param name="Scenario">The scenario.</param>
/// <param name="ResetTick">The tick the reset (stop, pose, reseed) is pinned to.</param>
/// <param name="ZeroTick">The absolute tick of the scenario's tick 0: the reset plus the settle.</param>
/// <param name="Lines">Every console line the scenario pins, by absolute tick, in the order they run.</param>
/// <param name="Stills">The stills, by tick.</param>
public sealed record MotionPlannedScenario(MotionScenario Scenario, ulong ResetTick, ulong ZeroTick, IReadOnlyList<(ulong Tick, string Line)> Lines, IReadOnlyList<MotionStill> Stills);
/// <summary>
/// A whole <c>puck motion</c> run laid out on absolute World ticks: the layout switch, then each selected scenario in
/// order, each starting from a reset body. The ticks depend only on the scenarios and <see cref="StartTick"/>, never on
/// how long the World took to boot, so two Worlds given one start tick pin every line and still to the same ticks.
/// </summary>
/// <param name="StartTick">The absolute tick the layout switch is pinned to.</param>
/// <param name="LastTick">The last tick any line is pinned to.</param>
/// <param name="Layout">The layout the run captures through.</param>
/// <param name="Scenarios">The scenarios, in run order.</param>
public sealed record MotionPlan(ulong StartTick, ulong LastTick, string Layout, IReadOnlyList<MotionPlannedScenario> Scenarios) {
    /// <summary>The ticks between the layout switch and the first reset, which a new camera view needs to adapt its exposure.</summary>
    public const int LayoutTicks = 150;
    /// <summary>The ticks after a reset's pose and stop at which the suit press runs, once the body is grounded.</summary>
    public const int SuitPressTick = 3;

    /// <summary>Lays scenarios out on absolute ticks.</summary>
    /// <param name="set">The scenario file's content, for its layout.</param>
    /// <param name="selected">The scenarios to run, in order.</param>
    /// <param name="startTick">The absolute tick the layout switch is pinned to; later than any tick the World has completed.</param>
    /// <param name="stillsRoot">The directory the stills' files are written under; each scenario gets a <c>&lt;name&gt;/raw</c> folder in it.</param>
    /// <returns>The plan.</returns>
    public static MotionPlan Build(MotionScenarioSet set, IReadOnlyList<MotionScenario> selected, ulong startTick, string stillsRoot) {
        ArgumentNullException.ThrowIfNull(argument: set);
        ArgumentNullException.ThrowIfNull(argument: selected);

        var planned = new List<MotionPlannedScenario>(capacity: selected.Count);
        var form = MotionForm.Suited;
        var reset = (startTick + LayoutTicks);
        var last = startTick;

        foreach (var scenario in selected) {
            var zero = (reset + ((ulong)scenario.Settle));
            var lines = new List<(ulong Tick, string Line)> {
                (reset, "body.stop 0"),
                (reset, scenario.Pose.ToConsoleLine()),
                ((reset + 1UL), "view.reseed 0"),
            };

            if (form != scenario.Form) {
                lines.Add(item: ((reset + ((ulong)SuitPressTick)), "body.press suit 1 0.2 0"));
            }

            lines.Add(item: (zero, "body.rig 0"));

            var stills = new List<MotionStill>(capacity: scenario.Capture.Count);
            var steps = scenario.Steps.Select(selector: step => ((zero + ((ulong)step.At)), step.Run));
            var captures = new List<(ulong Tick, string Line)>();

            foreach (var offset in scenario.Capture) {
                var stem = $"{stillsRoot}/{scenario.Name}/raw/t{offset.ToString(format: "D4", provider: CultureInfo.InvariantCulture)}";
                var tick = (zero + ((ulong)offset));

                stills.Add(item: new MotionStill(Offset: offset, Stem: stem, Tick: tick));
                captures.Add(item: (tick, "body.where 0"));
                captures.Add(item: (tick, $"world.screenshot {stem}.png slots"));
            }

            // Pinned lines of one tick run in the order they are armed: the steps first, so a still shows the state the
            // step's tick produced, then the reads and the capture.
            lines.AddRange(collection: steps);
            lines.AddRange(collection: captures);

            var ordered = lines.Select(selector: static (line, index) => (line.Tick, line.Line, Index: index)).OrderBy(keySelector: static line => line.Tick).ThenBy(keySelector: static line => line.Index).Select(selector: static line => (line.Tick, line.Line)).ToArray();

            planned.Add(item: new MotionPlannedScenario(Lines: ordered, ResetTick: reset, Scenario: scenario, Stills: stills, ZeroTick: zero));
            form = scenario.EndForm;
            last = ordered[^1].Tick;
            reset = (((zero + ((ulong)scenario.LastTick)) + 1UL) + ((ulong)scenario.Tail));
        }

        return new MotionPlan(LastTick: last, Layout: set.Layout, Scenarios: planned, StartTick: startTick);
    }
    /// <summary>Returns the <c>world.at</c> lines that arm the run: one per scenario, and the layout switch with the first.</summary>
    /// <returns>Each line, in the order they are sent.</returns>
    public IReadOnlyList<string> ArmingLines() {
        var arming = new List<string>(capacity: Scenarios.Count);

        for (var index = 0; (index < Scenarios.Count); index++) {
            var scenario = Scenarios[index];
            var origin = ((index == 0) ? StartTick : scenario.ResetTick);
            var lines = ((index == 0)
                ? [(StartTick, $"view.override layout {Layout}"), .. scenario.Lines]
                : scenario.Lines
            );

            arming.Add(item: ArmLine(lines: lines, start: origin));
        }

        return arming;
    }
    /// <summary>Formats one <c>world.at</c> script.</summary>
    /// <param name="start">The absolute tick offsets are counted from.</param>
    /// <param name="lines">The lines with their absolute ticks, none before <paramref name="start"/>.</param>
    /// <returns>A single-line <c>world.at {…}</c> command.</returns>
    public static string ArmLine(ulong start, IReadOnlyList<(ulong Tick, string Line)> lines) {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: stream)) {
            writer.WriteStartObject();
            writer.WriteNumber(propertyName: "start", value: start);
            writer.WriteStartArray(propertyName: "steps");

            foreach (var (tick, line) in lines) {
                writer.WriteStartObject();
                writer.WriteNumber(propertyName: "at", value: (tick - start));
                writer.WriteString(propertyName: "run", value: line);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return $"world.at {Encoding.UTF8.GetString(bytes: stream.ToArray())}";
    }
}
