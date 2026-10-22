using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Puck.Cli.Motion;

/// <summary>Which form the Moth wears when a motion scenario starts.</summary>
public enum MotionForm {
    /// <summary>In the mech suit (the register <c>suit</c> is 1): the 2.06 m Moth, who runs, flies and jumps twice.</summary>
    Suited,
    /// <summary>Out of the suit (the register is 0): the 1.68 m pilot, who walks, runs and jumps once.</summary>
    Pilot,
}
/// <summary>Where a motion scenario stands body 0 before it starts: a point and heading, or a declared spawn point.</summary>
/// <param name="Spawn">The <c>spawnPoints</c> id (<c>body.pose spawn:&lt;id&gt;</c>), or <see langword="null"/> for a point.</param>
/// <param name="X">The world X of a point.</param>
/// <param name="Y">The world Y of a point.</param>
/// <param name="Z">The world Z of a point.</param>
/// <param name="YawDegrees">The heading of a point, in degrees; 0 faces -Z.</param>
public sealed record MotionPose(string? Spawn, double X, double Y, double Z, double YawDegrees) {
    /// <summary>Returns the console line that puts body 0 here.</summary>
    /// <returns>A <c>body.pose</c> line.</returns>
    public string ToConsoleLine() => ((Spawn is null)
        ? string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"body.pose {X} {Y} {Z} {YawDegrees} 0 0 0"
        )
        : $"body.pose spawn:{Spawn} 0"
    );
}
/// <summary>One console line a scenario runs at a tick after its motion starts.</summary>
/// <param name="At">The tick, counted from the scenario's motion start (tick 0).</param>
/// <param name="Run">The console line.</param>
public sealed record MotionStep(int At, string Run);
/// <summary>One motion study: a start pose and form, tick-pinned steps and the ticks to capture.</summary>
/// <param name="Name">The scenario's name; the folder its stills and sheets are written to.</param>
/// <param name="Note">What the scenario shows and what a reviewer looks for.</param>
/// <param name="Form">The form body 0 starts in.</param>
/// <param name="Pose">The start pose.</param>
/// <param name="Settle">The ticks body 0 stands still after the reset and before tick 0, so its animation is at rest.</param>
/// <param name="Tail">The ticks the scenario runs after its last step or capture before the next scenario resets.</param>
/// <param name="Steps">The pinned console lines, by tick.</param>
/// <param name="Capture">The ticks to capture, ascending and distinct, counted from tick 0.</param>
public sealed record MotionScenario(string Name, string Note, MotionForm Form, MotionPose Pose, int Settle, int Tail, IReadOnlyList<MotionStep> Steps, IReadOnlyList<int> Capture) {
    /// <summary>Gets the form body 0 is in when the scenario ends: its start form, flipped once for each step that
    /// presses the suit channel.</summary>
    public MotionForm EndForm => (((Steps.Count(predicate: static step => MotionScenarioFile.TogglesSuit(line: step.Run)) % 2) == 0)
        ? Form
        : ((Form == MotionForm.Suited) ? MotionForm.Pilot : MotionForm.Suited)
    );
    /// <summary>Gets the last tick, counted from tick 0, at which the scenario does anything.</summary>
    public int LastTick => Math.Max(
        val1: ((Steps.Count == 0) ? 0 : Steps.Max(selector: static step => step.At)),
        val2: Capture[^1]
    );
}
/// <summary>A motion scenario file: the layout the stills are taken in and the scenarios, in run order.</summary>
/// <param name="Layout">The <c>views</c> layout every scenario captures through (<c>view.override layout</c>).</param>
/// <param name="Scenarios">The scenarios.</param>
public sealed record MotionScenarioSet(string Layout, IReadOnlyList<MotionScenario> Scenarios);
/// <summary>
/// Reads a motion scenario file (<c>puck.motion.scenarios.v1</c>), the format <c>puck motion</c> runs. The file is
/// strict JSON, parsed strictly: a member the format does not name is refused.
/// <code>
/// {
///   "schema": "puck.motion.scenarios.v1",
///   "layout": "motion",
///   "settle": 45,
///   "pose": [0, 0, 0, 0],
///   "scenarios": [
///     {
///       "name": "walk-start",
///       "note": "What to look for.",
///       "form": "suited",
///       "steps": [ { "at": 0, "run": "body.fly 1 0 0 0 0 0 2" } ],
///       "capture": [0, 3, { "from": 6, "to": 30, "step": 6 }]
///     }
///   ]
/// }
/// </code>
/// </summary>
public static partial class MotionScenarioFile {
    /// <summary>The schema string every scenario file carries.</summary>
    public const string Schema = "puck.motion.scenarios.v1";
    /// <summary>The fewest ticks a scenario may settle: the reset's pose, reseed and suit press need them.</summary>
    public const int MinimumSettle = 8;
    /// <summary>The most ticks any step or capture may be placed after tick 0.</summary>
    public const int MaximumTick = 1800;

    private static readonly string[] ScenarioMembers = ["capture", "form", "name", "note", "pose", "settle", "spawn", "steps", "tail"];
    private static readonly string[] SetMembers = ["layout", "pose", "scenarios", "schema", "settle", "tail"];

    [GeneratedRegex(pattern: "^[a-z0-9]+(-[a-z0-9]+)*$", options: RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
    [GeneratedRegex(pattern: @"^\s*body\.press\s+suit(\s|$)", options: RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SuitPress();

    /// <summary>Tests whether a console line presses the suit channel, which takes the suit off or puts it back on.</summary>
    /// <param name="line">The console line.</param>
    /// <returns><see langword="true"/> for <c>body.press suit …</c>.</returns>
    public static bool TogglesSuit(string line) => SuitPress().IsMatch(input: line);
    /// <summary>Reads a scenario file's text.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="set">The scenarios, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="error">Why the text is not a scenario file, or empty.</param>
    /// <returns><see langword="true"/> when the text is a valid scenario file.</returns>
    public static bool TryParse(string json, [NotNullWhen(returnValue: true)] out MotionScenarioSet? set, out string error) {
        set = null;

        try {
            using var document = JsonDocument.Parse(json: json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) {
                error = "the file is not a JSON object";

                return false;
            }
            if (!TryMembers(allowed: SetMembers, element: root, error: out error, where: "the file")) {
                return false;
            }
            if (!root.TryGetProperty(propertyName: "schema", value: out var schema) || (schema.ValueKind != JsonValueKind.String) || (schema.GetString() != Schema)) {
                error = $"the file's schema is not '{Schema}'";

                return false;
            }

            var layout = "motion";

            if (root.TryGetProperty(propertyName: "layout", value: out var layoutValue)) {
                if ((layoutValue.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: layoutValue.GetString())) {
                    error = "layout is a layout name";

                    return false;
                }

                layout = layoutValue.GetString()!;
            }

            var pose = new MotionPose(Spawn: null, X: 0d, Y: 0d, YawDegrees: 0d, Z: 0d);

            if (root.TryGetProperty(propertyName: "pose", value: out var poseValue) && !TryPose(element: poseValue, error: out error, pose: out pose!, where: "the file's pose")) {
                return false;
            }
            if (!TryTicks(defaultValue: 45, element: root, error: out error, name: "settle", value: out var settle, where: "the file") ||
                !TryTicks(defaultValue: 0, element: root, error: out error, name: "tail", value: out var tail, where: "the file")) {
                return false;
            }
            if (!root.TryGetProperty(propertyName: "scenarios", value: out var scenarios) || (scenarios.ValueKind != JsonValueKind.Array) || (scenarios.GetArrayLength() == 0)) {
                error = "scenarios is a non-empty array";

                return false;
            }

            var parsed = new List<MotionScenario>(capacity: scenarios.GetArrayLength());

            foreach (var scenario in scenarios.EnumerateArray()) {
                if (!TryScenario(defaultPose: pose, defaultSettle: settle, defaultTail: tail, element: scenario, error: out error, scenario: out var one)) {
                    return false;
                }
                if (parsed.Any(predicate: other => (other.Name == one.Name))) {
                    error = $"scenario '{one.Name}' appears twice";

                    return false;
                }

                parsed.Add(item: one);
            }

            set = new MotionScenarioSet(Layout: layout, Scenarios: parsed);
            error = string.Empty;

            return true;
        } catch (JsonException exception) {
            error = $"not JSON: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }

    private static bool TryMembers(JsonElement element, string[] allowed, string where, out string error) {
        foreach (var member in element.EnumerateObject()) {
            if (!allowed.Contains(value: member.Name, comparer: StringComparer.Ordinal)) {
                error = $"{where} has no member '{member.Name}' (it has {string.Join(separator: ", ", value: allowed)})";

                return false;
            }
        }

        error = string.Empty;

        return true;
    }
    private static bool TryNumber(JsonElement element, string where, out double value, out string error) {
        value = 0d;
        error = string.Empty;

        if ((element.ValueKind != JsonValueKind.Number) || !element.TryGetDouble(value: out value) || !double.IsFinite(d: value)) {
            error = $"{where} is a number";

            return false;
        }

        return true;
    }
    private static bool TryWholeTick(JsonElement element, int maximum, string where, out int value, out string error) {
        value = 0;
        error = string.Empty;

        if ((element.ValueKind != JsonValueKind.Number) || !element.TryGetInt32(value: out value) || (value < 0) || (value > maximum)) {
            error = $"{where} is a whole number of ticks from 0 to {maximum.ToString(provider: CultureInfo.InvariantCulture)}";

            return false;
        }

        return true;
    }
    private static bool TryTicks(JsonElement element, string name, int defaultValue, string where, out int value, out string error) {
        value = defaultValue;
        error = string.Empty;

        return (!element.TryGetProperty(propertyName: name, value: out var member) ||
            TryWholeTick(element: member, error: out error, maximum: MaximumTick, value: out value, where: $"{where}'s {name}"));
    }
    private static bool TryPose(JsonElement element, string where, out MotionPose? pose, out string error) {
        pose = null;

        if ((element.ValueKind != JsonValueKind.Array) || (element.GetArrayLength() != 4)) {
            error = $"{where} is [x, y, z, yawDegrees]";

            return false;
        }

        var numbers = new double[4];
        var index = 0;

        foreach (var number in element.EnumerateArray()) {
            if (!TryNumber(element: number, error: out error, value: out numbers[index], where: $"{where}[{index.ToString(provider: CultureInfo.InvariantCulture)}]")) {
                return false;
            }

            index++;
        }

        pose = new MotionPose(Spawn: null, X: numbers[0], Y: numbers[1], Z: numbers[2], YawDegrees: numbers[3]);
        error = string.Empty;

        return true;
    }
    private static bool TryCapture(JsonElement element, string where, out IReadOnlyList<int> ticks, out string error) {
        ticks = [];

        if ((element.ValueKind != JsonValueKind.Array) || (element.GetArrayLength() == 0)) {
            error = $"{where} is a non-empty array of ticks and {{from, to, step}} ranges";

            return false;
        }

        var found = new SortedSet<int>();

        foreach (var item in element.EnumerateArray()) {
            if (item.ValueKind == JsonValueKind.Object) {
                if (!TryMembers(allowed: ["from", "step", "to"], element: item, error: out error, where: $"{where}'s range")) {
                    return false;
                }
                if (!item.TryGetProperty(propertyName: "from", value: out var from) ||
                    !item.TryGetProperty(propertyName: "to", value: out var to) ||
                    !item.TryGetProperty(propertyName: "step", value: out var step) ||
                    !TryWholeTick(element: from, error: out error, maximum: MaximumTick, value: out var first, where: $"{where}'s range from") ||
                    !TryWholeTick(element: to, error: out error, maximum: MaximumTick, value: out var last, where: $"{where}'s range to") ||
                    !TryWholeTick(element: step, error: out error, maximum: MaximumTick, value: out var every, where: $"{where}'s range step")) {
                    error = ((error.Length == 0) ? $"{where}'s range names from, to and step" : error);

                    return false;
                }
                if ((every == 0) || (last < first)) {
                    error = $"{where}'s range steps forward by at least one tick from 'from' up to 'to'";

                    return false;
                }

                for (var tick = first; (tick <= last); tick += every) {
                    _ = found.Add(item: tick);
                }
            } else if (TryWholeTick(element: item, error: out error, maximum: MaximumTick, value: out var one, where: $"{where}'s tick")) {
                _ = found.Add(item: one);
            } else {
                return false;
            }
        }

        ticks = [.. found];
        error = string.Empty;

        return true;
    }
    private static bool TrySteps(JsonElement element, string where, out IReadOnlyList<MotionStep> steps, out string error) {
        steps = [];
        error = string.Empty;

        if (element.ValueKind != JsonValueKind.Array) {
            error = $"{where} is an array of {{at, run}}";

            return false;
        }

        var found = new List<MotionStep>();

        foreach (var item in element.EnumerateArray()) {
            if ((item.ValueKind != JsonValueKind.Object) ||
                !TryMembers(allowed: ["at", "run"], element: item, error: out error, where: $"{where}'s step") ||
                !item.TryGetProperty(propertyName: "at", value: out var at) ||
                !TryWholeTick(element: at, error: out error, maximum: MaximumTick, value: out var tick, where: $"{where}'s step at") ||
                !item.TryGetProperty(propertyName: "run", value: out var run) ||
                (run.ValueKind != JsonValueKind.String) ||
                string.IsNullOrWhiteSpace(value: run.GetString())) {
                error = ((error.Length == 0) ? $"{where}'s step is {{\"at\": <tick>, \"run\": \"<console line>\"}}" : error);

                return false;
            }

            var line = run.GetString()!.Trim();
            var verb = line.Split(count: 2, separator: [' ', '\t'])[0];

            if ((line.IndexOfAny(anyOf: ['\r', '\n']) >= 0) || verb.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "world.at") ||
                verb.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "world.wait") || verb.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "quit") ||
                verb.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "world.screenshot")) {
                error = $"{where}'s step '{line}' is not a single pinnable line (world.at, world.wait, quit and world.screenshot belong to the verb; list the tick under capture)";

                return false;
            }

            found.Add(item: new MotionStep(At: tick, Run: line));
        }

        steps = [.. found.OrderBy(keySelector: static step => step.At)];

        return true;
    }
    private static bool TryScenario(JsonElement element, MotionPose defaultPose, int defaultSettle, int defaultTail, out MotionScenario scenario, out string error) {
        scenario = null!;

        if (element.ValueKind != JsonValueKind.Object) {
            error = "a scenario is a JSON object";

            return false;
        }
        if (!TryMembers(allowed: ScenarioMembers, element: element, error: out error, where: "a scenario")) {
            return false;
        }
        if (!element.TryGetProperty(propertyName: "name", value: out var nameValue) || (nameValue.ValueKind != JsonValueKind.String) || !NamePattern().IsMatch(input: nameValue.GetString()!)) {
            error = "a scenario's name is lowercase words joined by hyphens";

            return false;
        }

        var name = nameValue.GetString()!;
        var where = $"scenario '{name}'";
        var note = string.Empty;
        var form = MotionForm.Suited;
        var pose = defaultPose;

        if (element.TryGetProperty(propertyName: "note", value: out var noteValue)) {
            if (noteValue.ValueKind != JsonValueKind.String) {
                error = $"{where}'s note is text";

                return false;
            }

            note = noteValue.GetString()!;
        }
        if (element.TryGetProperty(propertyName: "form", value: out var formValue)) {
            var text = ((formValue.ValueKind == JsonValueKind.String) ? formValue.GetString() : null);

            if (text is not ("suited" or "pilot")) {
                error = $"{where}'s form is \"suited\" or \"pilot\"";

                return false;
            }

            form = ((text == "pilot") ? MotionForm.Pilot : MotionForm.Suited);
        }
        if (element.TryGetProperty(propertyName: "pose", value: out var poseValue) && !TryPose(element: poseValue, error: out error, pose: out pose!, where: $"{where}'s pose")) {
            return false;
        }
        if (element.TryGetProperty(propertyName: "spawn", value: out var spawn)) {
            if ((spawn.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: spawn.GetString()) || element.TryGetProperty(propertyName: "pose", value: out _)) {
                error = $"{where}'s spawn is a spawnPoints id, and replaces pose";

                return false;
            }

            pose = new MotionPose(Spawn: spawn.GetString(), X: 0d, Y: 0d, Z: 0d, YawDegrees: 0d);
        }
        if (!TryTicks(defaultValue: defaultSettle, element: element, error: out error, name: "settle", value: out var settle, where: where) ||
            !TryTicks(defaultValue: defaultTail, element: element, error: out error, name: "tail", value: out var tail, where: where)) {
            return false;
        }
        if (settle < MinimumSettle) {
            error = $"{where}'s settle is at least {MinimumSettle.ToString(provider: CultureInfo.InvariantCulture)} ticks";

            return false;
        }

        IReadOnlyList<MotionStep> steps = [];

        if (element.TryGetProperty(propertyName: "steps", value: out var stepsValue) && !TrySteps(element: stepsValue, error: out error, steps: out steps, where: where)) {
            return false;
        }
        if (!element.TryGetProperty(propertyName: "capture", value: out var captureValue)) {
            error = $"{where} names no capture ticks";

            return false;
        }
        if (!TryCapture(element: captureValue, error: out error, ticks: out var capture, where: $"{where}'s capture")) {
            return false;
        }

        scenario = new MotionScenario(Capture: capture, Form: form, Name: name, Note: note, Pose: pose, Settle: settle, Steps: steps, Tail: tail);
        error = string.Empty;

        return true;
    }
}
