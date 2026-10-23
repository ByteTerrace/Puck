using System.Globalization;

namespace Puck.Cli.Motion;

/// <summary>Reads what a World wrote while a plan ran, and judges each scenario against the plan: every still announced and
/// served at its tick with a file for every camera, no pinned line refused, and body 0 in the declared form at tick 0.</summary>
public static class MotionCollector {
    /// <summary>Collects a run's outcome from the World's output and writes its contact sheets.</summary>
    /// <param name="directory">The run's directory; the sheets are written under it and the files are named relative to it.</param>
    /// <param name="plan">The plan the World ran.</param>
    /// <param name="lines">Every line the World wrote, on either stream, in arrival order.</param>
    /// <param name="columns">The most frames in a band of a contact sheet.</param>
    /// <param name="cellWidth">The width each frame of a contact sheet is scaled to, in pixels.</param>
    /// <param name="bootTick">The tick the World was ready at.</param>
    /// <param name="seconds">How long the run took.</param>
    /// <param name="notes">The warnings that do not fail the run.</param>
    /// <param name="clearance">The camera clearance check, or <see langword="null"/> to make none.</param>
    /// <returns>The outcome, with each scenario's problems.</returns>
    public static MotionRunResult Collect(string directory, MotionPlan plan, IReadOnlyList<string> lines, int columns, int cellWidth, ulong bootTick, double seconds, IReadOnlyList<string> notes, IMotionClearance? clearance = null) {
        var stamps = new List<MotionStillStamp>();
        var slots = new List<MotionSlotFile>();
        var pinned = new List<MotionPinnedAnswer>();

        foreach (var line in lines) {
            if (MotionTranscript.TryParseStill(line: line, stamp: out var stamp)) {
                stamps.Add(item: stamp);
            } else if (MotionTranscript.TryParseSlot(line: line, slot: out var slot)) {
                slots.Add(item: slot);
            } else if (MotionTranscript.TryParsePinned(answer: out var answer, line: line)) {
                pinned.Add(item: answer);
            }
        }

        List<string> cameras = [];
        var results = new List<MotionScenarioResult>(capacity: plan.Scenarios.Count);

        foreach (var planned in plan.Scenarios) {
            var problems = new List<string>();
            var stillResults = new List<MotionStillResult>(capacity: planned.Stills.Count);
            var last = (planned.ZeroTick + ((ulong)planned.Scenario.LastTick));
            var scenarioCameras = slots.Where(predicate: slot => planned.Stills.Any(predicate: still => slot.Path.StartsWith(value: (still.Stem + "-"), comparisonType: StringComparison.OrdinalIgnoreCase))).Select(selector: static slot => slot.Camera).Distinct(comparer: StringComparer.Ordinal).ToArray();

            cameras.AddRange(collection: scenarioCameras.Except(second: cameras));

            foreach (var refused in pinned.Where(predicate: answer => (answer.Refused && (answer.Tick >= planned.ArmTick) && (answer.Tick <= last)))) {
                problems.Add(item: $"tick {refused.Tick.ToString(provider: CultureInfo.InvariantCulture)}: '{refused.Line}' was refused: {refused.Answer}");
            }

            var observed = string.Empty;

            if (pinned.FirstOrDefault(predicate: answer => ((answer.Tick == planned.ZeroTick) && (answer.Line == "body.rig 0"))) is { } rig) {
                observed = (MotionTranscript.CreationOf(answer: rig.Answer) ?? string.Empty);

                var expected = ((planned.Scenario.Form == MotionForm.Suited) ? "moth" : "moth-pilot");

                if (observed != expected) {
                    problems.Add(item: $"body 0 wore '{observed}' at tick 0, not the '{expected}' of its {planned.Scenario.Form} form");
                }
            } else {
                problems.Add(item: "the form read at tick 0 never answered");
            }

            foreach (var still in planned.Stills) {
                var name = $"{still.Stem}.png";
                var stamp = stamps.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Path, b: name, comparisonType: StringComparison.OrdinalIgnoreCase));
                var files = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
                var where = $"tick {still.Tick.ToString(provider: CultureInfo.InvariantCulture)} (t+{still.Offset.ToString(provider: CultureInfo.InvariantCulture)})";

                foreach (var slot in slots.Where(predicate: slot => (slot.Path.StartsWith(value: (still.Stem + "-"), comparisonType: StringComparison.OrdinalIgnoreCase) && File.Exists(path: slot.Path)))) {
                    files[slot.Camera] = Path.GetRelativePath(path: slot.Path, relativeTo: directory).Replace(newChar: '/', oldChar: '\\');
                }

                if (stamp is null) {
                    problems.Add(item: $"{where}: the World announced no still");
                } else if (stamp.ShowsAnotherTick || (stamp.Shown != still.Tick.ToString(provider: CultureInfo.InvariantCulture))) {
                    problems.Add(item: $"{where}: the frame served shows tick {stamp.Shown}");
                }
                if (files.Count != scenarioCameras.Length) {
                    problems.Add(item: $"{where}: {files.Count.ToString(provider: CultureInfo.InvariantCulture)} of {scenarioCameras.Length.ToString(provider: CultureInfo.InvariantCulture)} camera files written");
                }

                string? Read(string line) => pinned.LastOrDefault(predicate: answer => ((answer.Tick == still.Tick) && (answer.Line == line)))?.Answer;

                var rigAnswer = Read(line: "body.rig 0");
                var gait = (((rigAnswer is not null) && MotionRig.TryParse(answer: rigAnswer, reading: out var parsed)) ? parsed : null);

                stillResults.Add(item: new MotionStillResult(Contacts: (Read(line: "world.contacts 1") ?? string.Empty), Hash: stamp?.Hash, Offset: still.Offset, Pose: stamp?.Pose, Rig: gait, Slots: files, Tick: still.Tick, Where: (Read(line: "body.where 0") ?? string.Empty)));
            }

            var clear = (clearance?.Check(cameras: scenarioCameras, scenario: planned, stills: stillResults) ?? []);

            problems.AddRange(collection: clear);

            // A scenario with a blocked camera writes no sheet: the stills it names are in the folder as evidence, never as a result.
            var sheets = ((clear.Count == 0) ? MotionSheetWriter.Write(cameras: scenarioCameras, cellWidth: cellWidth, columns: columns, directory: directory, problems: problems, scenario: planned, stills: stillResults) : ((IReadOnlyDictionary<string, string>)new Dictionary<string, string>()));

            results.Add(item: new MotionScenarioResult(Cameras: scenarioCameras, ObservedForm: observed, Planned: planned, Problems: problems, Sheets: sheets, Stills: stillResults));
        }

        return new MotionRunResult(
            BootTick: bootTick,
            Cameras: cameras,
            Directory: directory,
            Error: string.Empty,
            Notes: notes,
            Scenarios: results,
            Seconds: seconds,
            StartTick: plan.StartTick,
            TooLateStart: null
        );
    }
}
