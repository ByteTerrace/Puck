using System.Text;
using System.Text.Json.Nodes;
using Puck.Cli.Qualification;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>puck qualify</c> reads a <c>puck.release.profile.v1</c> document strictly and refuses one
/// qualification could not honor, naming the member: an unknown or missing member, a foreign schema tag, a backend no
/// leg boots, a validation layer on a backend the profile does not run, an odd resolution axis, a workload named twice
/// or booting anything but a package-relative <c>.world.json</c>, and a matrix cell with no threshold row, two, or a
/// pipeline threshold that disagrees with its workload. The checked-in profile loads. A valid profile expands into
/// backend-major cells, and each cell's script arms exactly the waits, readings, inspections and releases its
/// expectation counts, with a resize naming the exact extents its layout slot halves to.
/// </summary>
public sealed class ReleaseProfileLawTests {
    private const string Layout = """{"name":"pipeline","seatCount":0,"slots":[{"camera":null,"height":1,"pipeline":"ink","width":1,"x":0,"y":0}],"transitionRenderScale":1,"transitionSeconds":0.25}""";

    private static JsonObject Valid() => JsonNode.Parse(json: """
        {
          "schema": "puck.release.profile.v1",
          "publish": { "mode": "ReadyToRun", "entryAssembly": "Puck.World.dll", "compiler": "None" },
          "backends": ["vulkan", "directx"],
          "debugLayers": ["vulkan"],
          "functional": ["pipeline-feedback"],
          "resolutions": [{ "width": 1280, "height": 800 }],
          "workloads": [
            { "name": "world", "world": "Assets/worlds/puck.world.json", "warmupTicks": 30, "soakTicks": 60, "worldReloads": 2, "pipeline": null, "timeoutSeconds": 120 },
            { "name": "ink", "world": "Assets/worlds/pipeline.world.json", "warmupTicks": 30, "soakTicks": 60, "worldReloads": 0,
              "pipeline": { "instance": "ink", "layout": "pipeline", "settleFrames": 4, "reloads": 2, "resizes": 1, "loads": 3 }, "timeoutSeconds": 120 }
          ],
          "thresholds": [
            { "workload": "world", "backend": "vulkan", "width": 1280, "height": 800, "peakOwnedPipelineBytes": null },
            { "workload": "world", "backend": "directx", "width": 1280, "height": 800, "peakOwnedPipelineBytes": null },
            { "workload": "ink", "backend": "vulkan", "width": 1280, "height": 800, "peakOwnedPipelineBytes": 155648000 },
            { "workload": "ink", "backend": "directx", "width": 1280, "height": 800, "peakOwnedPipelineBytes": 155648000 }
          ],
          "deferred": [{ "check": "FrameTime", "reason": "Timing is deferred." }]
        }
        """)!.AsObject();
    private static bool TryParse(JsonObject document, out ReleaseProfile? profile, out string reason) => ReleaseProfileLoader.TryParse(
        bytes: Encoding.UTF8.GetBytes(s: document.ToJsonString()),
        profile: out profile,
        reason: out reason
    );
    private static string Refusal(Action<JsonObject> edit) {
        var document = Valid();

        edit(obj: document);

        Assert.False(condition: TryParse(
            document: document,
            profile: out _,
            reason: out var reason
        ));

        return reason;
    }
    private static ReleaseProfile Profile() {
        Assert.True(condition: TryParse(
            document: Valid(),
            profile: out var profile,
            reason: out var reason
        ), userMessage: reason);

        return profile!;
    }
    private static QualificationScript Script(QualificationCell cell) {
        var rows = ((cell.Workload.Pipeline is null)
            ? null
            : new QualificationPipelineRows(
                Layout: JsonNode.Parse(json: Layout)!.AsObject(),
                Source: "../pipelines/ink.pipeline.json"
            ));

        Assert.True(condition: QualificationPlan.TryWrite(
            cell: cell,
            reason: out var reason,
            rows: rows,
            script: out var script
        ), userMessage: reason);

        return script!;
    }

    [Fact]
    public void TheCheckedInProfileLoads() {
        Assert.True(condition: ReleaseProfileLoader.TryLoad(
            path: RepositoryPaths.Resolve(relativePath: ReleaseProfileLoader.DefaultPath),
            profile: out var profile,
            reason: out var reason
        ), userMessage: reason);
        Assert.Equal(actual: profile.Publish.Mode, expected: ReleasePublishMode.ReadyToRun);
        Assert.Equal(actual: profile.Publish.Compiler, expected: ReleaseCompilerDiscovery.None);
        Assert.Equal(
            actual: QualificationPlan.Cells(profile: profile).Count,
            expected: ((profile.Backends.Count * profile.Workloads.Count) * profile.Resolutions.Count)
        );
        Assert.Contains(collection: profile.Deferred, filter: static deferral => (deferral.Check == QualificationDeferredCheck.DeviceLocalPeak));
    }
    [Fact]
    public void AStrictParseRefusesUnknownAndMissingMembersAndForeignValues() {
        Assert.Contains(expectedSubstring: "surprise", actualString: Refusal(edit: static document => document["surprise"] = 1));
        Assert.Contains(expectedSubstring: "deferred", actualString: Refusal(edit: static document => document.Remove(propertyName: "deferred")));
        Assert.Contains(expectedSubstring: "$.publish.mode", actualString: Refusal(edit: static document => document["publish"]!["mode"] = "NativeAot"));
        Assert.Contains(expectedSubstring: "$.publish.compiler", actualString: Refusal(edit: static document => document["publish"]!["compiler"] = "Maybe"));
        Assert.Contains(expectedSubstring: "schema", actualString: Refusal(edit: static document => document["schema"] = "puck.release.profile.v0"));
        Assert.Contains(expectedSubstring: "entryAssembly", actualString: Refusal(edit: static document => document["publish"]!["entryAssembly"] = "bin/Puck.World.dll"));
    }
    [Fact]
    public void BackendsAndLayersMustBeOnesALegBoots() {
        Assert.Contains(expectedSubstring: "'metal'", actualString: Refusal(edit: static document => document["backends"]!.AsArray().Add(value: "metal")));
        Assert.Contains(expectedSubstring: "listed twice", actualString: Refusal(edit: static document => document["backends"]!.AsArray().Add(value: "vulkan")));
        Assert.Contains(expectedSubstring: "debugLayers", actualString: Refusal(edit: static document => {
            document["backends"] = new JsonArray("vulkan");
            document["debugLayers"] = new JsonArray("directx");
        }));
        Assert.Contains(expectedSubstring: "functional", actualString: Refusal(edit: static document => document["functional"]!.AsArray().Add(value: "pipeline-feedback")));
    }
    [Fact]
    public void AResolutionMustHalveExactly() {
        Assert.Contains(expectedSubstring: "odd axis", actualString: Refusal(edit: static document => document["resolutions"]![0]!["width"] = 1281));
        Assert.Contains(expectedSubstring: "outside", actualString: Refusal(edit: static document => document["resolutions"]![0]!["height"] = 0));
    }
    [Fact]
    public void AWorkloadIsNamedOnceAndBootsAPackageRelativeWorldDocument() {
        Assert.Contains(expectedSubstring: "listed twice", actualString: Refusal(edit: static document => document["workloads"]![1]!["name"] = "world"));
        Assert.Contains(expectedSubstring: "lowercase", actualString: Refusal(edit: static document => document["workloads"]![0]!["name"] = "World"));
        Assert.Contains(expectedSubstring: ".world.json", actualString: Refusal(edit: static document => document["workloads"]![0]!["world"] = "Assets/worlds/puck.puck"));
        Assert.Contains(expectedSubstring: "package-relative", actualString: Refusal(edit: static document => document["workloads"]![0]!["world"] = "../worlds/puck.world.json"));
        Assert.Contains(expectedSubstring: "soakTicks", actualString: Refusal(edit: static document => document["workloads"]![0]!["soakTicks"] = 0));
        Assert.Contains(expectedSubstring: "settleFrames", actualString: Refusal(edit: static document => document["workloads"]![1]!["pipeline"]!["settleFrames"] = 0));
    }
    [Fact]
    public void EveryCellHasExactlyOneThresholdRowAgreeingWithItsWorkload() {
        Assert.Contains(expectedSubstring: "has no threshold row", actualString: Refusal(edit: static document => document["thresholds"]!.AsArray().RemoveAt(index: 3)));
        Assert.Contains(expectedSubstring: "two threshold rows", actualString: Refusal(edit: static document => document["thresholds"]!.AsArray().Add(value: document["thresholds"]![0]!.DeepClone())));
        Assert.Contains(expectedSubstring: "must be null", actualString: Refusal(edit: static document => document["thresholds"]![0]!["peakOwnedPipelineBytes"] = 1));
        Assert.Contains(expectedSubstring: "positive byte count", actualString: Refusal(edit: static document => document["thresholds"]![2]!["peakOwnedPipelineBytes"] = null));
        Assert.Contains(expectedSubstring: "not one of the profile's resolutions", actualString: Refusal(edit: static document => document["thresholds"]![3]!["width"] = 1920));
        Assert.Contains(expectedSubstring: "no workload", actualString: Refusal(edit: static document => document["thresholds"]![3]!["workload"] = "other"));
    }
    [Fact]
    public void ADeferralIsListedOnceWithAReason() {
        Assert.Contains(expectedSubstring: "no reason", actualString: Refusal(edit: static document => document["deferred"]![0]!["reason"] = " "));
        Assert.Contains(expectedSubstring: "listed twice", actualString: Refusal(edit: static document => document["deferred"]!.AsArray().Add(value: document["deferred"]![0]!.DeepClone())));
    }
    [Fact]
    public void TheMatrixIsBackendMajorWithEachCellsOwnThreshold() {
        var cells = QualificationPlan.Cells(profile: Profile());

        Assert.Equal(
            actual: cells.Select(selector: static cell => cell.Id),
            expected: ["world/1280x800/vulkan", "ink/1280x800/vulkan", "world/1280x800/directx", "ink/1280x800/directx"]
        );
        Assert.All(collection: cells, action: static cell => {
            Assert.Equal(actual: cell.Threshold.Workload, expected: cell.Workload.Name);
            Assert.Equal(actual: cell.Threshold.Backend, expected: cell.Backend);
        });
    }
    [Fact]
    public void AWorldCellSoaksTwiceAroundItsReloads() {
        var script = Script(cell: QualificationPlan.Cells(profile: Profile())[0]);
        var lines = script.Text.Split(separator: '\n');

        Assert.Equal(actual: script.Expectation.CountersReadings, expected: 4);
        Assert.Equal(actual: script.Expectation.SoakWindows, expected: [(0, 1), (2, 3)]);
        Assert.Equal(actual: script.Expectation.Inspections, expected: 0);
        Assert.Equal(actual: script.Expectation.Releases, expected: 0);
        Assert.Equal(actual: script.Expectation.ArmedWaits, expected: 0);
        Assert.Equal(actual: lines.Count(predicate: static line => (line == "world.reload")), expected: 2);
        Assert.Equal(actual: lines.Count(predicate: static line => (line == "world.counters --json")), expected: 4);
        Assert.Equal(actual: lines.Count(predicate: static line => (line == "world.wait 60")), expected: 2);
    }
    [Fact]
    public void APipelineCellArmsWhatItsExpectationCounts() {
        var cell = QualificationPlan.Cells(profile: Profile())[1];
        var script = Script(cell: cell);
        var lines = script.Text.Split(separator: '\n');
        var pipeline = cell.Workload.Pipeline!;

        Assert.Equal(actual: script.Expectation.Inspections, expected: (((2 + pipeline.Reloads) + (2 * pipeline.Resizes)) + pipeline.Loads));
        Assert.Equal(actual: lines.Count(predicate: static line => (line == "pipeline.inspect ink")), expected: (script.Expectation.Inspections + script.Expectation.Releases));
        Assert.Equal(actual: script.Expectation.Releases, expected: pipeline.Loads);
        Assert.Equal(actual: lines.Count(predicate: static line => (line == "world.row.remove views.pipelines ink")), expected: pipeline.Loads);
        Assert.Equal(actual: script.Expectation.ArmedWaits, expected: lines.Count(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "pipeline.wait ")));
        Assert.Equal(actual: script.Expectation.CountersReadings, expected: 4);
        Assert.Equal(actual: script.Expectation.SoakWindows, expected: [(0, 1), (2, 3)]);
        Assert.Equal(actual: lines.Count(predicate: static line => (line == "pipeline.reload ink")), expected: pipeline.Reloads);
        Assert.Contains(collection: lines, expected: "pipeline.wait ink resized 640 400 40");
        Assert.Contains(collection: lines, expected: "pipeline.wait ink resized 1280 800 40");
        Assert.Contains(collection: lines, expected: "pipeline.wait ink counted 4 40");
        Assert.Contains(collection: lines, expected: "pipeline.load ink ../pipelines/ink.pipeline.json");
        Assert.Contains(collection: lines, filter: static line => (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.row.set views.layouts ") && line.Contains(comparisonType: StringComparison.Ordinal, value: "\"pipeline\":null")));
        Assert.Contains(collection: lines, filter: static line => (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.row.set views.layouts ") && line.Contains(comparisonType: StringComparison.Ordinal, value: "\"width\":0.5")));
    }
    [Fact]
    public void AnOverlayBootsTheWorldOffscreenAtTheCellsExtent() {
        var cell = QualificationPlan.Cells(profile: Profile())[1];
        var overlay = JsonNode.Parse(json: QualificationPlan.Overlay(cell: cell))!;

        Assert.Equal(actual: ((string?)overlay["basis"]), expected: "pipeline");
        Assert.Equal(actual: ((string?)overlay["host"]!["presentation"]), expected: "offscreen");
        Assert.Equal(actual: ((int?)overlay["host"]!["width"]), expected: 1280);
        Assert.Equal(actual: ((int?)overlay["host"]!["height"]), expected: 800);
        Assert.Equal(actual: QualificationPlan.OverlayFileName(cell: cell), expected: "qualify-ink-1280x800.world.json");
    }
    [Fact]
    public void AScriptIsRefusedWhenItsSlotCannotHalveOrShowsNoInstance() {
        var cell = QualificationPlan.Cells(profile: Profile())[1];

        static QualificationPipelineRows Rows(string layout) => new(
            Layout: JsonNode.Parse(json: layout)!.AsObject(),
            Source: "ink.pipeline.json"
        );

        Assert.False(condition: QualificationPlan.TryWrite(
            cell: cell,
            reason: out var uneven,
            rows: Rows(layout: Layout.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"width\":0.99921875,", oldValue: "\"width\":1,")),
            script: out _
        ));
        Assert.Contains(actualString: uneven, expectedSubstring: "whole pixels");
        Assert.False(condition: QualificationPlan.TryWrite(
            cell: cell,
            reason: out var missing,
            rows: Rows(layout: Layout.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"pipeline\":\"other\"", oldValue: "\"pipeline\":\"ink\"")),
            script: out _
        ));
        Assert.Contains(actualString: missing, expectedSubstring: "no slot");
    }
    [Fact]
    public void RowsAreReadFromWhatTheWorldAuthors() {
        var pipeline = new QualificationPipeline(Instance: "ink", Layout: "pipeline", Loads: 1, Reloads: 1, Resizes: 1, SettleFrames: 4);
        var world = string.Concat(str0: """{"views":{"layouts":[""", str1: Layout, str2: """],"pipelines":[{"name":"ink","source":"../pipelines/ink.pipeline.json"}]}}""");

        Assert.True(condition: QualificationPlan.TryReadRows(
            pipeline: pipeline,
            reason: out var reason,
            rows: out var rows,
            worldText: world
        ), userMessage: reason);
        Assert.Equal(actual: rows!.Source, expected: "../pipelines/ink.pipeline.json");
        Assert.False(condition: QualificationPlan.TryReadRows(
            pipeline: (pipeline with { Layout = "absent" }),
            reason: out var noLayout,
            rows: out _,
            worldText: world
        ));
        Assert.Contains(actualString: noLayout, expectedSubstring: "views.layouts");
        Assert.False(condition: QualificationPlan.TryReadRows(
            pipeline: (pipeline with { Instance = "absent" }),
            reason: out var noPipeline,
            rows: out _,
            worldText: world
        ));
        Assert.Contains(actualString: noPipeline, expectedSubstring: "views.pipelines");
    }
}
