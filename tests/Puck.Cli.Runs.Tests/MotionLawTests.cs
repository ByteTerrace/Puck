using System.Text.Json;
using Puck.Cli.Motion;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>puck motion</c>'s pure parts. A scenario file parses strictly (an unknown member, a repeated
/// name or a line that belongs to the verb is refused by name) and expands capture ranges; a plan pins every line and
/// still to ticks that depend only on the scenarios and the start tick, in the order they run, toggling the suit only
/// when a scenario's form differs; the sheet layout puts a camera's frames in order across bands; the transcript
/// parsers read the lines the World writes; and the reproduce report counts pixels and names a pose mismatch.
/// </summary>
public sealed class MotionLawTests {
    private const string Valid = """
        {
          "schema": "puck.motion.scenarios.v1",
          "layout": "motion",
          "settle": 20,
          "pose": [1, 0, 2, 90],
          "scenarios": [
            { "name": "walk", "form": "suited", "steps": [ { "at": 2, "run": "body.fly 1 0 0 0 0 0 1" }, { "at": 0, "run": "body.press run 1 1" } ], "capture": [0, 4, { "from": 10, "to": 20, "step": 5 }] },
            { "name": "pilot-jump", "form": "pilot", "settle": 12, "tail": 3, "spawn": "stage", "steps": [ { "at": 1, "run": "body.press jump 1 0.3" } ], "capture": [1, 9] },
            { "name": "back-in", "form": "suited", "steps": [ { "at": 0, "run": "body.press suit 1 0.2" } ], "capture": [2] }
          ]
        }
        """;

    private static MotionScenarioSet Parse(string json) {
        Assert.True(condition: MotionScenarioFile.TryParse(error: out var error, json: json, set: out var set), userMessage: error);

        return set!;
    }
    private static string Refusal(string json) {
        Assert.False(condition: MotionScenarioFile.TryParse(error: out var error, json: json, set: out _));

        return error;
    }
    private static MotionImage Solid(int width, int height, byte value) => new(Height: height, Rgba: Enumerable.Repeat(count: ((width * height) * 4), element: value).ToArray(), Width: width);

    [Fact]
    public void AScenarioFileParsesWithItsDefaultsRangesAndStepOrder() {
        var set = Parse(json: Valid);

        Assert.Equal(expected: "motion", actual: set.Layout);
        Assert.Equal(expected: ["walk", "pilot-jump", "back-in"], actual: set.Scenarios.Select(selector: static scenario => scenario.Name).ToArray());

        var walk = set.Scenarios[0];

        Assert.Equal(expected: 20, actual: walk.Settle);
        Assert.Equal(expected: [0, 4, 10, 15, 20], actual: walk.Capture.ToArray());
        Assert.Equal(expected: [0, 2], actual: walk.Steps.Select(selector: static step => step.At).ToArray());
        Assert.Equal(expected: "body.pose 1 0 2 90 0 0 0", actual: walk.Pose.ToConsoleLine());
        Assert.Equal(expected: 20, actual: walk.LastTick);

        var jump = set.Scenarios[1];

        Assert.Equal(expected: MotionForm.Pilot, actual: jump.Form);
        Assert.Equal(expected: 12, actual: jump.Settle);
        Assert.Equal(expected: 3, actual: jump.Tail);
        Assert.Equal(expected: "body.pose spawn:stage 0", actual: jump.Pose.ToConsoleLine());
        Assert.Equal(expected: MotionForm.Pilot, actual: jump.EndForm);
    }
    [Fact]
    public void ASuitPressFlipsTheFormAScenarioEndsIn() {
        var set = Parse(json: Valid);

        Assert.Equal(expected: MotionForm.Pilot, actual: set.Scenarios[2].EndForm);
        Assert.True(condition: MotionScenarioFile.TogglesSuit(line: " body.press suit 1 0.2 0"));
        Assert.False(condition: MotionScenarioFile.TogglesSuit(line: "body.press suitcase"));
    }
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a","capture":[0],"colour":1}]}""", "no member 'colour'")]
    [InlineData("""{"schema":"puck.motion.scenarios.v2","scenarios":[]}""", "schema")]
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a","capture":[0]},{"name":"a","capture":[1]}]}""", "appears twice")]
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"A b","capture":[0]}]}""", "lowercase words")]
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a","capture":[]}]}""", "non-empty array")]
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a","capture":[0],"settle":3}]}""", "at least 8")]
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a","capture":[0],"steps":[{"at":0,"run":"world.screenshot x.png"}]}]}""", "not a single pinnable line")]
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a","capture":[0],"steps":[{"at":0,"run":"quit"}]}]}""", "not a single pinnable line")]
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a","capture":[{"from":9,"to":3,"step":1}]}]}""", "steps forward")]
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a","capture":[0],"form":"mech"}]}""", "\"suited\" or \"pilot\"")]
    [InlineData("""{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a","capture":[0],"pose":[0,0,0,0],"spawn":"stage"}]}""", "replaces pose")]
    [InlineData("not json", "not JSON")]
    [Theory]
    public void AMalformedScenarioFileIsRefusedByName(string json, string expected) => Assert.Contains(expectedSubstring: expected, actualString: Refusal(json: json));
    [Fact]
    public void APlanPinsEveryLineToTicksThatDependOnlyOnTheStartTick() {
        var set = Parse(json: Valid);
        var plan = MotionPlan.Build(selected: set.Scenarios, set: set, startTick: 1000UL, stillsRoot: "D:/out");
        var again = MotionPlan.Build(selected: set.Scenarios, set: set, startTick: 1000UL, stillsRoot: "D:/out");
        var later = MotionPlan.Build(selected: set.Scenarios, set: set, startTick: 1600UL, stillsRoot: "D:/out");

        Assert.Equal(expected: plan.ArmingLines(), actual: again.ArmingLines());
        Assert.Equal(expected: plan.Scenarios.Select(selector: static scenario => (scenario.ZeroTick + 600UL)).ToArray(), actual: later.Scenarios.Select(selector: static scenario => scenario.ZeroTick).ToArray());

        var walk = plan.Scenarios[0];

        Assert.Equal(expected: (1000UL + ((ulong)MotionPlan.LayoutTicks)), actual: walk.ResetTick);
        Assert.Equal(expected: (walk.ResetTick + 20UL), actual: walk.ZeroTick);
        Assert.Equal(expected: [0, 4, 10, 15, 20], actual: walk.Stills.Select(selector: static still => still.Offset).ToArray());
        Assert.Equal(expected: "D:/out/walk/raw/t0004", actual: walk.Stills[1].Stem);

        // The next scenario resets one tick past this one's last, plus its tail.
        Assert.Equal(expected: (walk.ZeroTick + 21UL), actual: plan.Scenarios[1].ResetTick);
        Assert.Equal(expected: (((plan.Scenarios[1].ZeroTick + 9UL) + 1UL) + 3UL), actual: plan.Scenarios[2].ResetTick);

        // Lines run in tick order, and at a tick the steps run before the reads and the capture.
        Assert.Equal(expected: walk.Lines.Select(selector: static line => line.Tick).Order().ToArray(), actual: walk.Lines.Select(selector: static line => line.Tick).ToArray());

        var atZero = walk.Lines.Where(predicate: line => (line.Tick == walk.ZeroTick)).Select(selector: static line => line.Line).ToArray();

        Assert.Equal(actual: atZero, expected: ["body.rig 0", "body.press run 1 1", "body.where 0", "body.rig 0", "world.contacts 1", "world.screenshot D:/out/walk/raw/t0000.png slots"]);
    }
    [Fact]
    public void APlanPressesTheSuitOnlyWhereTheFormChanges() {
        var set = Parse(json: Valid);
        var plan = MotionPlan.Build(selected: set.Scenarios, set: set, startTick: 500UL, stillsRoot: "D:/out");

        string[] Presses(MotionPlannedScenario scenario) => [.. scenario.Lines.Where(predicate: static line => MotionScenarioFile.TogglesSuit(line: line.Line)).Select(selector: static line => line.Line)];

        Assert.Empty(collection: Presses(scenario: plan.Scenarios[0]));
        Assert.Equal(expected: ["body.press suit 1 0.2 0"], actual: Presses(scenario: plan.Scenarios[1]));
        // The third scenario starts suited after the second ended a pilot, and presses once more of its own.
        Assert.Equal(expected: ["body.press suit 1 0.2 0", "body.press suit 1 0.2"], actual: Presses(scenario: plan.Scenarios[2]));
    }
    [Fact]
    public void TheArmingLinesAreSingleLineWorldAtScriptsWithOffsetsFromTheirStart() {
        var set = Parse(json: Valid);
        var plan = MotionPlan.Build(selected: set.Scenarios, set: set, startTick: 1000UL, stillsRoot: "D:/out");
        var lines = plan.ArmingLines();

        Assert.Equal(expected: set.Scenarios.Count, actual: lines.Count);
        Assert.All(collection: lines, action: static line => Assert.DoesNotContain(actualString: line, expectedSubstring: "\n"));

        using var first = JsonDocument.Parse(json: lines[0]["world.at ".Length..]);

        Assert.Equal(expected: 1000L, actual: first.RootElement.GetProperty(propertyName: "start").GetInt64());

        var steps = first.RootElement.GetProperty(propertyName: "steps").EnumerateArray().ToArray();

        Assert.Equal(expected: "view.override layout motion", actual: steps[0].GetProperty(propertyName: "run").GetString());
        Assert.Equal(expected: 0L, actual: steps[0].GetProperty(propertyName: "at").GetInt64());

        using var second = JsonDocument.Parse(json: lines[1]["world.at ".Length..]);

        Assert.Equal(expected: ((long)plan.Scenarios[1].ResetTick), actual: second.RootElement.GetProperty(propertyName: "start").GetInt64());
        Assert.Equal(expected: "body.stop 0", actual: second.RootElement.GetProperty(propertyName: "steps")[0].GetProperty(propertyName: "run").GetString());
    }
    [Fact]
    public void AScenarioThatChangesTheLayoutSwitchesAndWaitsForTheCamerasBeforeItsReset() {
        var set = Parse(json: """
            {"schema":"puck.motion.scenarios.v1","layout":"motion","scenarios":[
              {"name":"a","capture":[0]},
              {"name":"b","layout":"motion-course","capture":[0]},
              {"name":"c","layout":"motion-course","capture":[0]},
              {"name":"d","capture":[0]}
            ]}
            """);
        var plan = MotionPlan.Build(selected: set.Scenarios, set: set, startTick: 1000UL, stillsRoot: "D:/out");
        var layouts = plan.Scenarios.Select(selector: static scenario => scenario.Lines.Where(predicate: static line => line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "view.override layout ")).Select(selector: static line => line.Line["view.override layout ".Length..]).ToArray()).ToArray();

        Assert.Equal(expected: ["motion-course"], actual: set.Scenarios.Select(selector: static scenario => scenario.Layout).OfType<string>().Distinct().ToArray());
        Assert.Equal(actual: layouts, expected: [["motion"], ["motion-course"], [], ["motion"]]);

        var second = plan.Scenarios[1];
        var third = plan.Scenarios[2];

        Assert.Equal(expected: (second.ArmTick + ((ulong)MotionPlan.LayoutTicks)), actual: second.ResetTick);
        Assert.Equal(expected: third.ArmTick, actual: third.ResetTick);
        Assert.Equal(expected: second.ArmTick, actual: (plan.Scenarios[0].ZeroTick + 1UL));
        Assert.StartsWith(expectedStartString: $"world.at {{\"start\":{second.ArmTick}", actualString: plan.ArmingLines()[1]);
    }
    [Fact]
    public void ABodyRigReadGivesTheSpeedAndEachFootsPlantState() {
        const string Answer = "[body.rig: body:0 creation=moth speed=2.50 drivers=1 driver:pace phase=0.00 weight=1.00 effector:foot-left weight=1.00 planted=yes target=(1.00, 0.00, -2.00) align=1.00 normal=(0.00, 0.97, 0.26) effector:foot-right weight=0.50 planted=stepping target=none align=0.00 normal=(0.00, 1.00, 0.00)]";

        Assert.True(condition: MotionRig.TryParse(answer: Answer, reading: out var rig));
        Assert.Equal(expected: 2.5d, actual: rig.Speed);
        Assert.Equal(expected: ["foot-left", "foot-right"], actual: rig.Feet.Select(selector: static foot => foot.Name).ToArray());
        Assert.Equal(expected: ["yes", "stepping"], actual: rig.Feet.Select(selector: static foot => foot.Planted).ToArray());
        Assert.Equal(expected: "(0.00, 0.97, 0.26)", actual: rig.Feet[0].Normal);
        Assert.Equal(expected: "L plant R step | spd 2.50 | grounded", actual: MotionRig.Describe(facts: "grounded", rig: rig));
        Assert.Equal(expected: "airborne|falling", actual: MotionRig.FactsOf(answer: "[body.where: body:0 pos=(0, 1, 0) facts=airborne|falling home=(0, 0, 0)]"));
        Assert.Equal(expected: "airborne", actual: MotionRig.Describe(facts: "airborne", rig: null));
        Assert.False(condition: MotionRig.TryParse(answer: "[body.rig: a procedural catalog rig]", reading: out _));
        Assert.Equal(expected: "R free", actual: MotionRig.Describe(facts: string.Empty, rig: new MotionRigReading(Feet: [new MotionFoot(Name: "foot-right", Normal: "n", Planted: "no", Target: "none", Weight: 0d)], Speed: 0d)).Split(separator: " | ")[0]);
        Assert.Equal(expected: "profile t+4 tick 99\nL plant R step | spd 2.50 | grounded", actual: MotionSheetWriter.CellLabel(camera: "motion-profile", cellWidth: 480, still: new MotionStillResult(Contacts: string.Empty, Hash: null, Offset: 4, Pose: null, Rig: rig, Slots: new Dictionary<string, string>(), Tick: 99UL, Where: "[body.where: facts=grounded]")));
    }
    [InlineData(0UL, 300UL)]
    [InlineData(42UL, 300UL)]
    [InlineData(180UL, 300UL)]
    [InlineData(181UL, 600UL)]
    [Theory]
    public void WorldsThatBootWithinAGridStepPinToTheSameStartTick(ulong boot, ulong expected) => Assert.Equal(expected: expected, actual: MotionRun.StartFor(bootTick: boot));
    [Fact]
    public void ASheetLayoutReadsAFramesAcrossAndWrapsIntoBands() {
        var single = MotionSheetLayout.Plan(cameras: 1, cellHeight: 100, cellWidth: 200, columns: 4, frames: 3, labelHeight: 20);

        Assert.Equal(expected: (6 + (3 * 206)), actual: single.Width);
        Assert.Equal(expected: ((6 + 120) + 6), actual: single.Height);
        Assert.Equal(expected: [6, 212, 418], actual: single.Cells.Select(selector: static cell => cell.X).ToArray());

        var wrapped = MotionSheetLayout.Plan(cameras: 2, cellHeight: 100, cellWidth: 200, columns: 2, frames: 3, labelHeight: 20);

        // Two bands of two camera rows; frame 2 starts the second band, so it sits below the first band's last row.
        Assert.Equal(expected: (6 + (2 * 206)), actual: wrapped.Width);
        Assert.Equal(expected: (6 + (2 * ((2 * 120) + 6))), actual: wrapped.Height);

        var third = wrapped.Cells.Single(predicate: static cell => ((cell.Frame == 2) && (cell.Camera == 0)));
        var firstRow = wrapped.Cells.Single(predicate: static cell => ((cell.Frame == 0) && (cell.Camera == 1)));

        Assert.Equal(expected: 6, actual: third.X);
        Assert.Equal(expected: (((6 + (2 * 120)) + 6) + 20), actual: third.Y);
        Assert.Equal(expected: ((6 + 120) + 20), actual: firstRow.Y);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => MotionSheetLayout.Plan(cameras: 1, cellHeight: 1, cellWidth: 1, columns: 0, frames: 1, labelHeight: 1));
    }
    [Fact]
    public void AComposedSheetHoldsEachCellAtItsPlaceAndTheLabelStripAboveIt() {
        var layout = MotionSheetLayout.Plan(cameras: 1, cellHeight: 4, cellWidth: 6, columns: 2, frames: 2, labelHeight: 10);
        var sheet = MotionSheets.Compose(
            frames: [[Solid(height: 4, value: 200, width: 6)], [Solid(height: 4, value: 90, width: 6)]],
            labels: [["a"], ["b"]],
            layout: layout
        );

        Assert.Equal(expected: layout.Width, actual: sheet.Width);

        byte At(int x, int y) => sheet.Rgba[(((y * sheet.Width) + x) * 4)];

        Assert.Equal(expected: 200, actual: At(x: layout.Cells[0].X, y: layout.Cells[0].Y));
        Assert.Equal(expected: 90, actual: At(x: (layout.Cells[1].X + 5), y: (layout.Cells[1].Y + 3)));
        Assert.Equal(expected: "profile t+12 tick 3012", actual: MotionSheets.Label(camera: "motion-profile", offset: 12, tick: 3012UL));
    }
    [Fact]
    public void ScalingAveragesTheCoveredPixelsAndKeepsTheAspect() {
        var source = new MotionImage(Height: 2, Rgba: [0, 0, 0, 255, 100, 100, 100, 255, 100, 100, 100, 255, 200, 200, 200, 255], Width: 2);
        var scaled = MotionSheets.ScaleToWidth(source: source, width: 1);

        Assert.Equal(expected: (1, 1), actual: (scaled.Width, scaled.Height));
        Assert.Equal(expected: [100, 100, 100, 255], actual: scaled.Rgba);
        Assert.Equal(expected: (4, 2), actual: (MotionSheets.ScaleToWidth(source: Solid(height: 4, value: 1, width: 8), width: 4).Width, MotionSheets.ScaleToWidth(source: Solid(height: 4, value: 1, width: 8), width: 4).Height));
    }
    [Fact]
    public void AComparisonCountsDifferingPixelsTheirLargestStepAndTheMean() {
        var left = Solid(height: 1, value: 10, width: 2);
        var right = new MotionImage(Height: 1, Rgba: [10, 10, 10, 10, 14, 10, 10, 10], Width: 2);
        var difference = MotionSheets.Compare(left: left, right: right);

        Assert.Equal(expected: 1L, actual: difference!.Count);
        Assert.Equal(expected: 4, actual: difference.Max);
        Assert.Equal(expected: (4d / 8d), actual: difference.Mean);
        Assert.Equal(expected: 0L, actual: MotionSheets.Compare(left: left, right: left)!.Count);
        Assert.Null(@object: MotionSheets.Compare(left: left, right: Solid(height: 1, value: 10, width: 1)));
    }
    [Fact]
    public void TheTranscriptParsersReadTheLinesTheWorldWrites() {
        Assert.True(condition: MotionTranscript.TryParseStill(line: @"[still] D:\out\walk\raw\t0004.png tick=1234 hash=00ABCDEF pose=11223344 shown=1234", stamp: out var still));
        Assert.Equal(expected: ("D:/out/walk/raw/t0004.png", 1234UL, "00ABCDEF", "11223344", false), actual: (still.Path, still.Tick, still.Hash, still.Pose, still.ShowsAnotherTick));
        Assert.True(condition: MotionTranscript.TryParseStill(line: "[still] a b/c.png tick=7 hash=1 pose=2 shown=6 (the frame shows another tick)", stamp: out var other));
        Assert.True(condition: other.ShowsAnotherTick);
        Assert.Equal(expected: "a b/c.png", actual: other.Path);
        Assert.True(condition: MotionTranscript.TryParseSlot(line: "[still] slot motion-feet 960x540 at 0,540 -> D:/out/walk/raw/t0004-motion-feet.png", slot: out var slot));
        Assert.Equal(expected: ("motion-feet", "D:/out/walk/raw/t0004-motion-feet.png"), actual: (slot.Camera, slot.Path));
        Assert.False(condition: MotionTranscript.TryParseStill(line: "[still] slot motion-feet 960x540 at 0,540 -> x.png", stamp: out _));
        Assert.True(condition: MotionTranscript.TryParsePinned(answer: out var ran, line: "[world.at] tick 99 ran: body.where 0 -> [body.where: body:0 pos=(1, 2, 3)]"));
        Assert.Equal(expected: (99UL, false, "body.where 0", "[body.where: body:0 pos=(1, 2, 3)]"), actual: (ran.Tick, ran.Refused, ran.Line, ran.Answer));
        Assert.True(condition: MotionTranscript.TryParsePinned(answer: out var refused, line: "[world.at] tick 5 refused: body.nope 0 -> [unknown]"));
        Assert.True(condition: refused.Refused);
        Assert.True(condition: MotionTranscript.TryParsePinned(answer: out var quiet, line: "[world.at] tick 6 ran: view.reseed 0"));
        Assert.Equal(expected: string.Empty, actual: quiet.Answer);
        Assert.True(condition: MotionTranscript.TryParseReadBack(line: "[world.at: tick 812 pending 0 ran 0]", tick: out var tick));
        Assert.Equal(actual: tick, expected: 812UL);
        Assert.False(condition: MotionTranscript.TryParseReadBack(line: "[world.at: armed 3 line(s) at ticks 1..2 (now tick 0)]", tick: out _));
        Assert.Equal(expected: "moth-pilot", actual: MotionTranscript.CreationOf(answer: "[body.rig: body:0 creation=moth-pilot speed=0.00]"));
        Assert.Null(@object: MotionTranscript.CreationOf(answer: "[body.rig: a procedural catalog rig]"));
    }
    [Fact]
    public void AReproduceReportNamesAPoseMismatchAndCountsPixelsPerStill() {
        var rows = new MotionReproduceRow[] {
            new(Scenario: "walk", Offset: 0, Tick: 100UL, PoseEqual: true, HashEqual: true, Cameras: [new MotionCameraDifference(Camera: "a", Difference: new MotionPixelDifference(Count: 0L, Max: 0, Mean: 0d))]),
            new(Scenario: "walk", Offset: 5, Tick: 105UL, PoseEqual: false, HashEqual: false, Cameras: [new MotionCameraDifference(Camera: "a", Difference: new MotionPixelDifference(Count: 7L, Max: 90, Mean: 0.25d)), new MotionCameraDifference(Camera: "b", Difference: new MotionPixelDifference(Count: 3L, Max: 40, Mean: 0.75d))]),
        };

        Assert.Equal(expected: (10L, 90, 0.5d), actual: (rows[1].Count, rows[1].Max, rows[1].Mean));

        var table = MotionReport.FormatTable(rows: rows);

        Assert.Contains(actualString: table, expectedSubstring: "DIFF");
        Assert.Contains(actualString: table, expectedSubstring: "2 still(s): 1 with equal pose hashes, 1 without; 1 pixel-identical, 1 not");
        Assert.Null(@object: new MotionReproduceRow(Scenario: "x", Offset: 0, Tick: 1UL, PoseEqual: true, HashEqual: true, Cameras: [new MotionCameraDifference(Camera: "a", Difference: null)]).Count);
    }
}
