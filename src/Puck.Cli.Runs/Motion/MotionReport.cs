using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Puck.Cli.Motion;

/// <summary>One camera's pixel comparison of a still across two runs.</summary>
/// <param name="Camera">The camera.</param>
/// <param name="Difference">How the two images differ, or <see langword="null"/> when either file is missing or the sizes differ.</param>
public sealed record MotionCameraDifference(string Camera, MotionPixelDifference? Difference);
/// <summary>One still compared across two runs of the same scenarios.</summary>
/// <param name="Scenario">The scenario.</param>
/// <param name="Offset">The capture tick, counted from the scenario's tick 0.</param>
/// <param name="Tick">The absolute tick, equal in both runs.</param>
/// <param name="PoseEqual">Whether both runs stamped the same pose hash; a missing stamp is unequal.</param>
/// <param name="HashEqual">Whether both runs stamped the same capture hash.</param>
/// <param name="Cameras">The pixel comparison of each camera's file.</param>
public sealed record MotionReproduceRow(string Scenario, int Offset, ulong Tick, bool PoseEqual, bool HashEqual, IReadOnlyList<MotionCameraDifference> Cameras) {
    /// <summary>Gets the pixels that differ, summed over every camera, or <see langword="null"/> when a camera could not be compared.</summary>
    public long? Count => (Cameras.Any(predicate: static camera => (camera.Difference is null)) ? null : Cameras.Sum(selector: static camera => camera.Difference!.Count));
    /// <summary>Gets the largest channel difference over every camera, or <see langword="null"/> when a camera could not be compared.</summary>
    public int? Max => (Cameras.Any(predicate: static camera => (camera.Difference is null)) ? null : Cameras.Max(selector: static camera => camera.Difference!.Max));
    /// <summary>Gets the mean channel difference averaged over the cameras, or <see langword="null"/> when a camera could not be compared.</summary>
    public double? Mean => (Cameras.Any(predicate: static camera => (camera.Difference is null)) ? null : Cameras.Average(selector: static camera => camera.Difference!.Mean));
}
/// <summary>Compares two runs of one plan, still by still, and writes the manifests.</summary>
public static class MotionReport {
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Compares two runs of the same scenarios pinned to the same ticks.</summary>
    /// <param name="first">The first run.</param>
    /// <param name="second">The second run.</param>
    /// <returns>One row per still of the first run; a still the second run lacks compares as unequal in everything.</returns>
    public static IReadOnlyList<MotionReproduceRow> Compare(MotionRunResult first, MotionRunResult second) {
        var rows = new List<MotionReproduceRow>();

        foreach (var scenario in first.Scenarios) {
            var other = second.Scenarios.FirstOrDefault(predicate: candidate => (candidate.Planned.Scenario.Name == scenario.Planned.Scenario.Name));

            foreach (var still in scenario.Stills) {
                var match = other?.Stills.FirstOrDefault(predicate: candidate => (candidate.Tick == still.Tick));
                var cameras = new List<MotionCameraDifference>(capacity: first.Cameras.Count);

                foreach (var camera in first.Cameras) {
                    MotionPixelDifference? difference = null;

                    if ((match is not null) && still.Slots.TryGetValue(key: camera, value: out var left) && match.Slots.TryGetValue(key: camera, value: out var right)) {
                        var leftPath = Path.Combine(path1: first.Directory, path2: left);
                        var rightPath = Path.Combine(path1: second.Directory, path2: right);

                        if (File.Exists(path: leftPath) && File.Exists(path: rightPath)) {
                            difference = MotionSheets.Compare(left: MotionSheets.Read(path: leftPath), right: MotionSheets.Read(path: rightPath));
                        }
                    }

                    cameras.Add(item: new MotionCameraDifference(Camera: camera, Difference: difference));
                }

                rows.Add(item: new MotionReproduceRow(
                    Cameras: cameras,
                    HashEqual: ((match is not null) && (still.Hash is not null) && (still.Hash == match.Hash)),
                    Offset: still.Offset,
                    PoseEqual: ((match is not null) && (still.Pose is not null) && (still.Pose == match.Pose)),
                    Scenario: scenario.Planned.Scenario.Name,
                    Tick: still.Tick
                ));
            }
        }

        return rows;
    }
    /// <summary>Formats the comparison as the table <c>puck motion --reproduce</c> prints.</summary>
    /// <param name="rows">The comparison.</param>
    /// <returns>A header and one line per still, then a summary line.</returns>
    public static string FormatTable(IReadOnlyList<MotionReproduceRow> rows) {
        var text = new StringBuilder();
        var width = Math.Max(val1: 8, val2: ((rows.Count == 0) ? 8 : rows.Max(selector: static row => row.Scenario.Length)));

        _ = text.AppendLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{"scenario".PadRight(totalWidth: width)} {"t+",5} {"tick",7}  {"pose",-5} {"hash",-5} {"px differ",10} {"max",4} {"mean",7}"));

        foreach (var row in rows) {
            var count = ((row.Count is { } differing) ? differing.ToString(provider: CultureInfo.InvariantCulture) : "n/a");
            var max = ((row.Max is { } largest) ? largest.ToString(provider: CultureInfo.InvariantCulture) : "n/a");
            var mean = ((row.Mean is { } average) ? average.ToString(format: "0.000", provider: CultureInfo.InvariantCulture) : "n/a");

            _ = text.AppendLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{row.Scenario.PadRight(totalWidth: width)} {row.Offset,5} {row.Tick,7}  {(row.PoseEqual ? "same" : "DIFF"),-5} {(row.HashEqual ? "same" : "diff"),-5} {count,10} {max,4} {mean,7}"));
        }

        var poseMismatches = rows.Count(predicate: static row => !row.PoseEqual);
        var identical = rows.Count(predicate: static row => (row.Count == 0));

        _ = text.Append(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{rows.Count} still(s): {(rows.Count - poseMismatches)} with equal pose hashes, {poseMismatches} without; {identical} pixel-identical, {(rows.Count - identical)} not (pixel differences are reported, not judged)."));

        return text.ToString();
    }
    /// <summary>Writes a run's manifest.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="result">The run.</param>
    /// <param name="commit">The commit the World was built at.</param>
    /// <param name="scenariosFile">The scenario file's path as the run was given it.</param>
    /// <param name="world">The world that was booted.</param>
    /// <param name="layout">The layout the stills were captured through.</param>
    public static void WriteManifest(string path, MotionRunResult result, string commit, string scenariosFile, string world, string layout) {
        var manifest = new {
            schema = "puck.motion.manifest.v1",
            commit,
            world,
            layout,
            scenariosFile,
            bootTick = result.BootTick,
            startTick = result.StartTick,
            seconds = Math.Round(value: result.Seconds, digits: 1),
            cameras = result.Cameras,
            notes = result.Notes,
            scenarios = result.Scenarios.Select(selector: static scenario => new {
                name = scenario.Planned.Scenario.Name,
                note = scenario.Planned.Scenario.Note,
                form = scenario.Planned.Scenario.Form.ToString().ToLowerInvariant(),
                observedForm = scenario.ObservedForm,
                resetTick = scenario.Planned.ResetTick,
                zeroTick = scenario.Planned.ZeroTick,
                steps = scenario.Planned.Scenario.Steps.Select(selector: static step => new { at = step.At, run = step.Run }),
                sheets = scenario.Sheets,
                problems = scenario.Problems,
                stills = scenario.Stills.Select(selector: static still => new {
                    offset = still.Offset,
                    tick = still.Tick,
                    hash = still.Hash,
                    pose = still.Pose,
                    where = still.Where,
                    files = still.Slots,
                }),
            }),
        };

        File.WriteAllText(contents: $"{JsonSerializer.Serialize(options: Options, value: manifest)}\n", encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), path: path);
    }
    /// <summary>Writes the comparison of two runs.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="rows">The comparison.</param>
    /// <param name="first">The first run's directory, relative to the file's folder.</param>
    /// <param name="second">The second run's directory, relative to the file's folder.</param>
    public static void WriteReproduce(string path, IReadOnlyList<MotionReproduceRow> rows, string first, string second) {
        var document = new {
            schema = "puck.motion.reproduce.v1",
            first,
            second,
            stills = rows.Select(selector: static row => new {
                scenario = row.Scenario,
                offset = row.Offset,
                tick = row.Tick,
                poseEqual = row.PoseEqual,
                hashEqual = row.HashEqual,
                differingPixels = row.Count,
                maxDifference = row.Max,
                meanDifference = row.Mean,
                cameras = row.Cameras.Select(selector: static camera => new {
                    camera = camera.Camera,
                    differingPixels = camera.Difference?.Count,
                    maxDifference = camera.Difference?.Max,
                    meanDifference = camera.Difference?.Mean,
                }),
            }),
        };

        File.WriteAllText(contents: $"{JsonSerializer.Serialize(options: Options, value: document)}\n", encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), path: path);
    }
}
