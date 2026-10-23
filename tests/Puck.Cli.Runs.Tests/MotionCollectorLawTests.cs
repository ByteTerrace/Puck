using System.Globalization;
using System.Text.Json;
using Puck.Assets;
using Puck.Cli.Motion;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// CONTRACT UNDER TEST: what <c>puck motion</c> makes of the lines a World wrote. A scenario whose stills were all
/// announced, served at their ticks and written for every camera, with no pinned line refused and body 0 in the declared
/// form, has no problems and gets a combined sheet and one per camera; each failure is named in the scenario's problems;
/// and the manifest and the reproduce report written from the outcome carry each still's hashes and pixel counts.
/// </summary>
public sealed class MotionCollectorLawTests : IDisposable {
    private static readonly string[] Cameras = ["motion-profile", "motion-front"];

    private readonly DirectoryInfo m_directory = Directory.CreateTempSubdirectory(prefix: "puck-motion-law-");

    private string Root => m_directory.FullName.Replace(newChar: '/', oldChar: '\\');

    private static MotionPlan PlanOf(string root, MotionForm form = MotionForm.Suited) {
        var set = new MotionScenarioSet(Layout: "motion", Scenarios: [
            new MotionScenario(Name: "walk", Note: "n", Form: form, Pose: new MotionPose(Spawn: null, X: 0d, Y: 0d, YawDegrees: 0d, Z: 0d), Settle: 10, Tail: 0, Steps: [], Capture: [0, 2]),
        ]);

        return MotionPlan.Build(selected: set.Scenarios, set: set, startTick: 100UL, stillsRoot: root);
    }
    private static string Pixel(string path, byte value) {
        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
        PngEncoder.Write(height: 2, path: path, rgba: Enumerable.Repeat(count: ((4 * 2) * 4), element: value).ToArray(), width: 4);

        return path;
    }
    // The lines a healthy World writes for the plan's one scenario, writing the files they name.
    private List<string> Lines(MotionPlan plan, byte value, string creation = "moth", bool announceSecond = true, string shownOffset = "", bool dropFile = false) {
        var scenario = plan.Scenarios[0];
        var lines = new List<string> {
            $"[world.at] tick {scenario.ZeroTick.ToString(provider: CultureInfo.InvariantCulture)} ran: body.rig 0 -> [body.rig: body:0 creation={creation} speed=0.00]",
        };

        foreach (var still in scenario.Stills) {
            var tick = still.Tick.ToString(provider: CultureInfo.InvariantCulture);

            lines.Add(item: $"[world.at] tick {tick} ran: body.where 0 -> [body.where: body:0 pos=(0.00, 0.00, 0.00) vel=(0.00, 0.00, 0.00)]");

            if (!announceSecond && (still.Offset == 2)) {
                continue;
            }

            lines.Add(item: $"[still] {still.Stem}.png tick={tick} hash=aa{tick} pose=bb{still.Offset.ToString(provider: CultureInfo.InvariantCulture)} shown={tick}{shownOffset}");

            foreach (var camera in Cameras) {
                var path = $"{still.Stem}-{camera}.png";

                if (!dropFile || (camera != "motion-front")) {
                    _ = Pixel(path: path, value: value);
                } else if (File.Exists(path: path)) {
                    File.Delete(path: path);
                }

                lines.Add(item: $"[still] slot {camera} 4x2 at 0,0 -> {path}");
            }
        }

        return lines;
    }

    public void Dispose() => m_directory.Delete(recursive: true);
    [Fact]
    public void AHealthyRunHasNoProblemsAndASheetPerCameraAndOneCombined() {
        var plan = PlanOf(root: Root);
        var result = MotionCollector.Collect(bootTick: 42UL, cellWidth: 16, columns: 4, directory: Root, lines: Lines(plan: plan, value: 10), notes: [], plan: plan, seconds: 1.5d);
        var scenario = Assert.Single(collection: result.Scenarios);

        Assert.True(condition: result.Clean, userMessage: string.Join(separator: "; ", values: scenario.Problems));
        Assert.Equal(expected: Cameras, actual: result.Cameras);
        Assert.Equal(expected: ["combined", "motion-profile", "motion-front"], actual: scenario.Sheets.Keys.ToArray());
        Assert.All(collection: scenario.Sheets.Values, action: sheet => Assert.True(condition: File.Exists(path: Path.Combine(path1: Root, path2: sheet))));
        Assert.Equal(expected: "moth", actual: scenario.ObservedForm);
        Assert.Equal(expected: "walk/raw/t0000-motion-profile.png", actual: scenario.Stills[0].Slots["motion-profile"]);
        Assert.Equal(expected: (("aa" + plan.Scenarios[0].Stills[1].Tick.ToString(provider: CultureInfo.InvariantCulture)), "bb2"), actual: (scenario.Stills[1].Hash, scenario.Stills[1].Pose));
        Assert.Contains(expectedSubstring: "body.where", actualString: scenario.Stills[0].Where);
    }
    [Fact]
    public void EachFailureIsNamedInTheScenariosProblems() {
        var plan = PlanOf(root: Root);

        string[] Problems(List<string> lines) => [.. MotionCollector.Collect(bootTick: 0UL, cellWidth: 16, columns: 4, directory: Root, lines: lines, notes: [], plan: plan, seconds: 0d).Scenarios[0].Problems];

        Assert.Contains(expectedSubstring: "announced no still", actualString: string.Join(separator: "|", values: Problems(lines: Lines(plan: plan, value: 5, announceSecond: false))));
        Assert.Contains(expectedSubstring: "shows tick", actualString: string.Join(separator: "|", values: Problems(lines: Lines(plan: plan, value: 5, shownOffset: " (the frame shows another tick)"))));
        Assert.Contains(expectedSubstring: "1 of 2 camera files", actualString: string.Join(separator: "|", values: Problems(lines: Lines(plan: plan, value: 5, dropFile: true))));
        Assert.Contains(expectedSubstring: "wore 'moth-pilot'", actualString: string.Join(separator: "|", values: Problems(lines: Lines(plan: plan, value: 5, creation: "moth-pilot"))));

        var refused = Lines(plan: plan, value: 5);

        refused.Add(item: $"[world.at] tick {plan.Scenarios[0].ZeroTick.ToString(provider: CultureInfo.InvariantCulture)} refused: body.nope 0 -> [unknown verb]");
        Assert.Contains(expectedSubstring: "was refused: [unknown verb]", actualString: string.Join(separator: "|", values: Problems(lines: refused)));
        Assert.Contains(expectedSubstring: "never answered", actualString: string.Join(separator: "|", values: Problems(lines: [])));
    }

    private sealed class Blocking(string message) : IMotionClearance {
        public IReadOnlyList<string> Check(MotionPlannedScenario scenario, IReadOnlyList<MotionStillResult> stills, IReadOnlyList<string> cameras) => [$"scenario '{scenario.Scenario.Name}' {message}"];
    }

    [Fact]
    public void ABlockedCameraFailsTheScenarioByNameAndWritesNoSheet() {
        var plan = PlanOf(root: Root);
        var clean = MotionCollector.Collect(bootTick: 0UL, cellWidth: 16, clearance: new Blocking(message: "x"), columns: 4, directory: Root, lines: Lines(plan: plan, value: 10), notes: [], plan: plan, seconds: 0d);
        var scenario = Assert.Single(collection: clean.Scenarios);

        Assert.False(condition: clean.Clean);
        Assert.Equal(expected: ["scenario 'walk' x"], actual: scenario.Problems.ToArray());
        Assert.Empty(collection: scenario.Sheets);
        Assert.False(condition: File.Exists(path: Path.Combine(path1: Root, path2: "walk/sheet.png")));
    }
    [Fact]
    public void TheManifestAndTheReproduceReportCarryEachStillsHashesAndPixelCounts() {
        var plan = PlanOf(root: Root);
        var first = MotionCollector.Collect(bootTick: 7UL, cellWidth: 16, columns: 4, directory: Root, lines: Lines(plan: plan, value: 10), notes: ["a note"], plan: plan, seconds: 1d);
        var otherRoot = $"{Root}/b";
        var otherPlan = PlanOf(root: otherRoot);
        var second = MotionCollector.Collect(bootTick: 9UL, cellWidth: 16, columns: 4, directory: otherRoot, lines: Lines(plan: otherPlan, value: 30), notes: [], plan: otherPlan, seconds: 1d);
        var rows = MotionReport.Compare(first: first, second: second);

        Assert.Equal(expected: 2, actual: rows.Count);
        Assert.All(collection: rows, action: static row => Assert.True(condition: (row.PoseEqual && row.HashEqual)));
        Assert.Equal(expected: ((2L * 4) * 2), actual: rows[0].Count);
        Assert.Equal(expected: 20, actual: rows[0].Max);

        var manifest = Path.Combine(path1: Root, path2: "manifest.json");

        MotionReport.WriteManifest(commit: "abc", layout: "motion", path: manifest, result: first, scenariosFile: "s.json", world: "worlds/debug");

        using var document = JsonDocument.Parse(json: File.ReadAllText(path: manifest));
        var still = document.RootElement.GetProperty(propertyName: "scenarios")[0].GetProperty(propertyName: "stills")[1];

        Assert.Equal(expected: "puck.motion.manifest.v1", actual: document.RootElement.GetProperty(propertyName: "schema").GetString());
        Assert.Equal(expected: "bb2", actual: still.GetProperty(propertyName: "pose").GetString());
        Assert.Equal(expected: "walk/raw/t0002-motion-front.png", actual: still.GetProperty(propertyName: "files").GetProperty(propertyName: "motion-front").GetString());

        var reproduce = Path.Combine(path1: Root, path2: "reproduce.json");

        MotionReport.WriteReproduce(first: "run-a", path: reproduce, rows: rows, second: "run-b");

        using var report = JsonDocument.Parse(json: File.ReadAllText(path: reproduce));

        Assert.Equal(expected: 16L, actual: report.RootElement.GetProperty(propertyName: "stills")[0].GetProperty(propertyName: "differingPixels").GetInt64());
        Assert.Equal(expected: 20, actual: report.RootElement.GetProperty(propertyName: "maximumDifferingPixelsPerStill").GetInt32());
        Assert.Equal(expected: 1, actual: report.RootElement.GetProperty(propertyName: "maximumChannelDifference").GetInt32());
        Assert.False(condition: report.RootElement.GetProperty(propertyName: "stills")[0].GetProperty(propertyName: "pixelsWithinBound").GetBoolean());
        Assert.False(condition: report.RootElement.GetProperty(propertyName: "stills")[0].GetProperty(propertyName: "reproduced").GetBoolean());

        // A still the second run lacks compares as unequal in everything.
        var lacking = MotionReport.Compare(first: first, second: MotionCollector.Collect(bootTick: 0UL, cellWidth: 16, columns: 4, directory: otherRoot, lines: [], notes: [], plan: otherPlan, seconds: 0d));

        Assert.All(collection: lacking, action: static row => Assert.False(condition: row.PoseEqual));
        Assert.All(collection: lacking, action: static row => Assert.Null(@object: row.Count));
    }
}
