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
    /// <summary>Gets whether every camera was compared and the still stays within the parked per-render residue.</summary>
    public bool PixelsWithinBound => ((Cameras.Count != 0) && (Count is <= MotionReport.MaximumResiduePixels) && (Max is <= MotionReport.MaximumResidueLevel));
    /// <summary>Gets whether both state hashes match and the pixels stay within the residue bound.</summary>
    public bool Reproduced => (PoseEqual && HashEqual && PixelsWithinBound);
    /// <summary>Gets the pixels that differ, summed over every camera, or <see langword="null"/> when a camera could not be compared.</summary>
    public long? Count => (((Cameras.Count == 0) || Cameras.Any(predicate: static camera => (camera.Difference is null))) ? null : Cameras.Sum(selector: static camera => camera.Difference!.Count));
    /// <summary>Gets the largest channel difference over every camera, or <see langword="null"/> when a camera could not be compared.</summary>
    public int? Max => (((Cameras.Count == 0) || Cameras.Any(predicate: static camera => (camera.Difference is null))) ? null : Cameras.Max(selector: static camera => camera.Difference!.Max));
    /// <summary>Gets the mean channel difference averaged over the cameras, or <see langword="null"/> when a camera could not be compared.</summary>
    public double? Mean => (((Cameras.Count == 0) || Cameras.Any(predicate: static camera => (camera.Difference is null))) ? null : Cameras.Average(selector: static camera => camera.Difference!.Mean));
}
/// <summary>Compares two runs of one plan, still by still, and writes the manifests.</summary>
public static class MotionReport {
    /// <summary>The most changed pixels admitted per still, summed over all camera files, for the parked per-render residue.</summary>
    public const int MaximumResiduePixels = 20;
    /// <summary>The largest absolute channel difference admitted for the parked per-render residue.</summary>
    public const int MaximumResidueLevel = 1;

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
                var cameras = new List<MotionCameraDifference>(capacity: scenario.Cameras.Count);

                foreach (var camera in scenario.Cameras) {
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

        _ = text.AppendLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{"scenario".PadRight(totalWidth: width)} {"t+",5} {"tick",7}  {"pose",-5} {"hash",-5} {"px differ",10} {"max",4} {"mean",7}  verdict"));

        foreach (var row in rows) {
            var count = ((row.Count is { } differing) ? differing.ToString(provider: CultureInfo.InvariantCulture) : "n/a");
            var max = ((row.Max is { } largest) ? largest.ToString(provider: CultureInfo.InvariantCulture) : "n/a");
            var mean = ((row.Mean is { } average) ? average.ToString(format: "0.000", provider: CultureInfo.InvariantCulture) : "n/a");

            _ = text.AppendLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{row.Scenario.PadRight(totalWidth: width)} {row.Offset,5} {row.Tick,7}  {(row.PoseEqual ? "same" : "DIFF"),-5} {(row.HashEqual ? "same" : "DIFF"),-5} {count,10} {max,4} {mean,7}  {(row.Reproduced ? "PASS" : "FAIL")}"));
        }

        var poseMismatches = rows.Count(predicate: static row => !row.PoseEqual);
        var identical = rows.Count(predicate: static row => (row.Count == 0));

        _ = text.Append(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{rows.Count} still(s): {(rows.Count - poseMismatches)} with equal pose hashes, {poseMismatches} without; {identical} pixel-identical, {(rows.Count - identical)} not; {rows.Count(predicate: static row => row.Reproduced)} reproduced, {rows.Count(predicate: static row => !row.Reproduced)} failed. Pixel bound: at most {MaximumResiduePixels} changed pixels across all cameras per still, at most {MaximumResidueLevel} channel level."));

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
                layout = scenario.Planned.Scenario.Layout,
                cameras = scenario.Cameras,
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
                    speed = still.Rig?.Speed,
                    feet = still.Rig?.Feet.Select(selector: static foot => new { name = foot.Name, planted = foot.Planted, weight = foot.Weight, target = foot.Target, normal = foot.Normal }),
                    contacts = still.Contacts,
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
            maximumDifferingPixelsPerStill = MaximumResiduePixels,
            maximumChannelDifference = MaximumResidueLevel,
            stills = rows.Select(selector: static row => new {
                scenario = row.Scenario,
                offset = row.Offset,
                tick = row.Tick,
                poseEqual = row.PoseEqual,
                hashEqual = row.HashEqual,
                pixelsWithinBound = row.PixelsWithinBound,
                reproduced = row.Reproduced,
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
