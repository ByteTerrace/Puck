using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Cli.Motion;
using Puck.Cli.Sweep;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>The proof door rejects absent or mismatched semantic workloads, independently of runner diagnostics.</summary>
public sealed class MotionRouteComparisonLawTests {
    private static readonly MotionRoutePlan Plan = new(MotionRoutePlan.SchemaName, "route", 1, 3, [], [], [], []);
    private static JsonObject Trace() => JsonSerializer.SerializeToNode(new {
        firstTick = 2, lastTick = 3, measureFirstTick = 2, sampleCount = 2, complete = true,
        missing = Array.Empty<int>(), duplicateTicks = Array.Empty<int>(), outOfOrderTicks = Array.Empty<int>(),
        elapsedMilliseconds = 1.0, unsupportedIdentityTypes = Array.Empty<string>(),
        samples = Enumerable.Range(2, 2).Select(tick => new {
            tick, clockWhole = tick, clockFractionBits = "0", timeBits = 0U, deltaBits = 0U, alphaBits = 0U,
            scope = "boot", documentId = "route", program = "program", views = "camera", transforms = "poses",
            viewCount = 1, transformCount = 2, width = 1280U, height = 720U,
            meshInstanceCount = 1, meshAssetSlotCount = 1, hostDrawCount = 0, unsupportedIdentityCount = 0, tickLocked = true, convergesLighting = false,
        }),
    })!.AsObject();
    private static MotionRouteLeg Leg(string trace, long work = 0) => new(
        Artifact: "World.dll", ArtifactSha256: "artifact", Directory: "leg", Recording: false,
        BuildFiles: new Dictionary<string, string>(), BuildSourceKey: null, ReplayStatus: "[replay.status: replaying route | tick 4 of 5 | no divergence]", Trace: trace,
        Before: new Dictionary<string, string> { ["work"] = work.ToString() }, After: new Dictionary<string, string>(),
        Problems: [], ProcessSeconds: 0) { DirectReceipts = BoundaryReceipts() };
    private static IReadOnlyList<MotionRouteReceipt> BoundaryReceipts(params MotionRouteReceipt[] readings) => [
        new("warmup-resume", "world.rate resume", new SweepAnswer(true, "", [])),
        new("warmup-wait", "world.wait until 1", new SweepAnswer(true, "", [])),
        new("warmup-paused", "world.rate pause", new SweepAnswer(true, "", [])),
        new("warmup-residency", "world.wait residency 600", new SweepAnswer(true, "[residency: settled]", [])),
        .. readings,
        new("trace-begin", "world.view.trace begin 2 3 2", new SweepAnswer(true, "", [])),
        new("measured-resume", "world.rate resume", new SweepAnswer(true, "", [])),
    ];
    private static IReadOnlyList<string> Compare(JsonObject trace) => MotionRouteReport.Compare(Leg(Trace().ToJsonString()), Leg(trace.ToJsonString()), Plan);

    [Fact]
    public void RouteValidationDistinguishesTheRateVerbFromRatesReadback() {
        var allowed = Plan with { Metrics = [new MotionRouteMetric("rates", "world.rates")] };
        Assert.Equal("world.rates", MotionRoutePlan.Parse(JsonSerializer.Serialize(allowed)).Metrics[0].Run);
        var quoted = Plan with { Metrics = [new MotionRouteMetric("rates", "\"world.rates\"")] };
        Assert.Single(MotionRoutePlan.Parse(JsonSerializer.Serialize(quoted)).Metrics);
        var forbidden = Plan with { Metrics = [new MotionRouteMetric("rate", "world.rate pause")] };
        Assert.Throws<InvalidDataException>(() => MotionRoutePlan.Parse(JsonSerializer.Serialize(forbidden)));
    }

    [Fact]
    public void FailedOrMissingTypedSetupCannotQualifyMatchingPresentation() {
        var plan = Plan with { Setup = ["world.cadence off"] };
        var trace = Trace().ToJsonString();
        var good = Leg(trace) with { DirectReceipts = [new("setup", "world.cadence off", new SweepAnswer(true, "[world.cadence: off]", [])), .. BoundaryReceipts()] };
        Assert.Empty(MotionRouteReport.Compare(good, good, plan));
        var failed = good with { DirectReceipts = [new("setup", "world.cadence off", new SweepAnswer(false, "unknown command", ["refused"])), .. BoundaryReceipts()] };
        Assert.NotEmpty(MotionRouteReport.Compare(good, failed, plan));
        Assert.NotEmpty(MotionRouteReport.Compare(good, Leg(trace), plan));
    }

    [Fact]
    public void RequestedSetupReadbacksMustHoldAtBothEndpoints() {
        var plan = Plan with { Checks = [new SweepSignal("cadence", "world.cadence", Red: ["text != [world.cadence: off]"])] };
        var good = Leg(Trace().ToJsonString()) with {
            DirectReceipts = BoundaryReceipts(new MotionRouteReceipt("before-check", "world.cadence", new SweepAnswer(true, "[world.cadence: off]", []))),
            BeforeChecks = new Dictionary<string, string> { ["cadence"] = "[world.cadence: off]" },
            AfterChecks = new Dictionary<string, string> { ["cadence"] = "[world.cadence: off]" },
        };
        Assert.Empty(MotionRouteReport.Compare(good, good, plan));
        var failed = good with { AfterChecks = new Dictionary<string, string> { ["cadence"] = "[world.cadence: on]" } };
        Assert.NotEmpty(MotionRouteReport.Compare(good, failed, plan));
        Assert.NotEmpty(MotionRouteReport.Compare(good, Leg(Trace().ToJsonString()), plan));
    }

    [Fact]
    public void PinnedReceiptsRequireActualCommandMultiplicityAndFaultsFail() {
        IReadOnlyList<(ulong Tick, string Line)> expected = [(1, "move"), (1, "move")];
        MotionPinnedAnswer receipt = new(1, false, "move", "");
        Assert.Empty(MotionRouteRun.CheckExecutionReceipts(expected, [receipt, receipt]));
        Assert.NotEmpty(MotionRouteRun.CheckExecutionReceipts(expected, [receipt]));
        Assert.NotEmpty(MotionRouteRun.CheckExecutionReceipts(expected, [receipt, new(1, false, "other", "")]));
        Assert.True(MotionTranscript.TryParsePinned("[world.at] tick 1 faulted: move -> failed", out var fault));
        Assert.True(fault!.Refused);
        Assert.NotEmpty(MotionRouteRun.CheckExecutionReceipts([(1, "move")], [fault!]));
    }

    [Theory]
    [InlineData(int.MaxValue, 600)]
    [InlineData(int.MinValue, 600)]
    [InlineData(2, 3)]
    [InlineData(1, int.MinValue)]
    public void RouteBoundsRejectOverflowAndEmptyMeasuredIntervals(int warmup, int last) {
        var json = JsonSerializer.Serialize(Plan with { WarmupTicks = warmup, LastTick = last });
        Assert.Throws<InvalidDataException>(() => MotionRoutePlan.Parse(json));
        Assert.Equal(3, MotionRoutePlan.Parse(JsonSerializer.Serialize(Plan)).LastTick);
    }

    [Fact]
    public void MissingAndMalformedTracesCannotPassAnOtherwiseCleanRunner() {
        Assert.NotEmpty(MotionRouteReport.Compare(Leg(""), Leg(""), Plan));
        Assert.NotEmpty(MotionRouteReport.Compare(Leg("{"), Leg(Trace().ToJsonString()), Plan));
        Assert.NotEmpty(MotionRouteReport.Compare(Leg("{\"samples\":[]}"), Leg(Trace().ToJsonString()), Plan));
        var trace = Trace();
        trace["samples"]![1]!.AsObject().Remove("transforms");
        Assert.Contains(Compare(trace), problem => problem.Contains("transforms", StringComparison.Ordinal));
    }

    [Fact]
    public void ActualTicksAndObserverBoundariesMustMatchTheRequestedWindow() {
        var repeated = Trace();
        repeated["samples"]![1]!["tick"] = 1;
        Assert.NotEmpty(Compare(repeated));
        var header = Trace();
        header["measureFirstTick"] = 3;
        Assert.NotEmpty(Compare(header));
        var duplicate = Trace();
        duplicate["duplicateTicks"]!.AsArray().Add(2);
        Assert.NotEmpty(Compare(duplicate));
        var policy = Trace();
        policy["samples"]![1]!["convergesLighting"] = true;
        Assert.NotEmpty(Compare(policy));
        var opaque = Trace();
        opaque["samples"]![1]!["unsupportedIdentityCount"] = 1;
        opaque["unsupportedIdentityTypes"]!.AsArray().Add("opaque");
        Assert.NotEmpty(Compare(opaque));
    }

    [Fact]
    public void SemanticDifferencesNameTheFirstTickAndWorkMayChange() {
        var changed = Trace();
        changed["samples"]![1]!["views"] = "another-camera";
        Assert.Contains(Compare(changed), problem => problem.Contains("tick 3, field 'views'", StringComparison.Ordinal));
        var trace = Trace().ToJsonString();
        Assert.Empty(MotionRouteReport.Compare(Leg(trace, 100), Leg(trace, 1), Plan));
    }

    [Fact]
    public void MatchingMeasuredSamplesRequireOrderedResidentWarmupReceipts() {
        var good = Leg(Trace().ToJsonString());
        Assert.Empty(MotionRouteReport.Compare(good, good, Plan));
        var missing = good with { DirectReceipts = good.DirectReceipts.Where(receipt => receipt.Stage != "warmup-residency").ToArray() };
        Assert.NotEmpty(MotionRouteReport.Compare(good, missing, Plan));
        var pending = good with { DirectReceipts = good.DirectReceipts.Select(receipt => receipt.Stage == "warmup-residency"
            ? receipt with { Result = new SweepAnswer(true, "[residency: pending]", []) } : receipt).ToArray() };
        Assert.NotEmpty(MotionRouteReport.Compare(good, pending, Plan));
        var reordered = good with { DirectReceipts = good.DirectReceipts.Reverse().ToArray() };
        Assert.NotEmpty(MotionRouteReport.Compare(good, reordered, Plan));
        var oldWindow = Trace();
        oldWindow["firstTick"] = 1;
        Assert.NotEmpty(Compare(oldWindow));
    }

    [Fact]
    public void MatchingPresentationRequiresBothMetricEndpointsButTheirValuesMayDiffer() {
        var plan = Plan with { Metrics = [new MotionRouteMetric("work", "world.counters", "text")] };
        var good = Leg(Trace().ToJsonString(), 100) with { After = new Dictionary<string, string> { ["work"] = "200" } };
        var changedWork = good with { Before = new Dictionary<string, string> { ["work"] = "50" }, After = new Dictionary<string, string> { ["work"] = "75" } };
        Assert.Empty(MotionRouteReport.Compare(good, changedWork, plan));
        Assert.DoesNotContain(good.DirectReceipts, receipt => receipt.Stage == "before-metric");
        var noBefore = good with { Before = new Dictionary<string, string>() };
        Assert.Contains(MotionRouteReport.Compare(good, noBefore, plan), problem => problem.Contains("endpoint reading 'work'", StringComparison.Ordinal));
        var noAfter = good with { After = new Dictionary<string, string>() };
        Assert.Contains(MotionRouteReport.Compare(good, noAfter, plan), problem => problem.Contains("endpoint reading 'work'", StringComparison.Ordinal));
        IReadOnlyList<(ulong Tick, string Line)> endpoints = [(2, "world.counters"), (4, "world.counters")];
        Assert.Empty(MotionRouteRun.CheckExecutionReceipts(endpoints, [new(2, false, "world.counters", "100"), new(4, false, "world.counters", "200")]));
        Assert.NotEmpty(MotionRouteRun.CheckExecutionReceipts(endpoints, [new(2, false, "world.counters", "100")]));
    }
}
