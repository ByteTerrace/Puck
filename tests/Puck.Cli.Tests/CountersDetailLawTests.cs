using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Cli.Counters;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Laws for detail identities through readings, comparison and device-aware ceilings.</summary>
public sealed class CountersDetailLawTests {
    [Fact]
    public void ReadingRetainsExecutedAndSkippedDetailsWithTheirPassClass() {
        using var json = JsonDocument.Parse(json: """
            {"sources":[],"gpu":{"device":{"backend":"vulkan","adapter":"GPU","vendor":1,"device":2,"driver":"d","driver.raw":0,"api":"a","driver.name":"","driver.id":0,"conformance":""},
            "nodes":[{"name":"world","sample":{"submission":1,"revision":1,"passes":[
            {"label":"sky","class":"per-backend-deterministic","state":"executed","counts":{"gpu.dispatches":3},"details":[{"label":"plain","counts":{"gpu.dispatches":1}},{"label":"stars","counts":{"gpu.dispatches":2}}]},
            {"label":"shadow","class":"deterministic","state":"skipped","details":[{"label":"plain"},{"label":"slot:0"}]}],"outside":{}},"lifetime":null}]},
            "allocation":{"gcMode":"test","windows":{}},"kinds":{"gpu.dispatches":{"unit":"count","class":"deterministic"}}}
            """);

        Assert.True(condition: CountersReading.TryRead(reading: json.RootElement, backend: "vulkan", width: 8, height: 8,
            compiler: "test", run: out var run, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: 3, actual: run.Counts.Count(predicate: static count => (count.Kind == "gpu.dispatches")));
        var stars = Assert.Single(collection: run.Counts, predicate: static count => (count.Detail == "stars"));

        Assert.Equal(expected: 2L, actual: stars.Value);
        Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: stars.Class);
        Assert.Equal(expected: new[] { "plain", "slot:0" }, actual: run.Passes[1].Details);
    }
    [Fact]
    public void EqualPassTotalsDoNotHideAChangedLayerCost() {
        var left = Report(run: Run(counts: [Count(detail: null, value: 9), Count(detail: "stars", value: 4), Count(detail: "clouds", value: 5)]));
        var right = Report(run: Run(counts: [Count(detail: null, value: 9), Count(detail: "stars", value: 3), Count(detail: "clouds", value: 6)]));
        var differences = CountersComparison.Reports(left: left, right: right);

        Assert.Equal(expected: 2, actual: differences.Count);
        Assert.Contains(collection: differences, filter: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: "detail=stars"));
        Assert.Contains(collection: differences, filter: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: "detail=clouds"));
    }
    // A detail's required zero is shared by every device: on a device with no record it is still judged, beside the line
    // naming the device, while that device's own magnitudes wait for its record. On the recording device every detail is
    // judged, and an unknown one fails.
    [Fact]
    public void ADetailRequiredZeroHoldsOnAForeignDeviceAndUnknownDetailsFail() {
        var original = Report(run: Run(counts: [Count(detail: null, value: 100), Count(detail: "stars", value: 0)]));
        var ceilings = CountersCeilings.Record(report: original);

        Assert.Equal(expected: 0L, actual: Assert.Single(collection: ceilings.Backends[0].Ceilings).Ceiling);
        var counts = new[] { Count(detail: null, value: 101), Count(detail: "stars", value: 1), Count(detail: "clouds", value: 3) };
        var foreign = CountersCeilings.Check(report: Report(run: Run(counts: counts) with { Device = Device with { DeviceId = 3 } }), ceilings: ceilings);

        Assert.Equal(expected: 2, actual: foreign.Failures.Count);
        Assert.Equal(expected: "vulkan: no ceilings recorded for GPU (vendor=0x0001 device=0x0003); run puck counters --record on it", actual: foreign.Failures[0]);
        Assert.Contains(collection: foreign.Failures, filter: static line => (line.Contains(comparisonType: StringComparison.Ordinal, value: "detail=stars") && line.Contains(comparisonType: StringComparison.Ordinal, value: "required zero")));
        Assert.Empty(collection: foreign.Notes);

        var recording = CountersCeilings.Check(report: Report(run: Run(counts: counts)), ceilings: ceilings);

        Assert.Equal(expected: 3, actual: recording.Failures.Count);
        Assert.Contains(collection: recording.Failures, filter: static line => (line.Contains(comparisonType: StringComparison.Ordinal, value: "detail=-") && line.Contains(comparisonType: StringComparison.Ordinal, value: "over its ceiling")));
        Assert.Contains(collection: recording.Failures, filter: static line => (line.Contains(comparisonType: StringComparison.Ordinal, value: "detail=clouds") && line.Contains(comparisonType: StringComparison.Ordinal, value: "no ceiling")));
    }
    [Fact]
    public void SkippedDetailsRecordZerosAndCannotDisappearFromAMeasurement() {
        var run = Run(counts: []) with {
            Passes = [new WorldCountersPass(Class: WorkClass.Deterministic, Label: "sky", Node: "world", State: GpuPassState.Skipped) { Details = ["plain", "stars"] }],
        };
        var report = Report(run: run);
        var ceilings = CountersCeilings.Record(report: report);

        Assert.Equal(expected: (GpuWork.SubmissionKinds.Length * 3), actual: ceilings.Backends[0].Ceilings.Count);
        Assert.Empty(collection: ceilings.Backends[0].Devices[0].Ceilings);
        Assert.Empty(collection: CountersCeilings.Check(ceilings: ceilings, report: report).Failures);
        var missing = run with { Passes = [run.Passes[0] with { Details = ["plain"] }] };

        Assert.Equal(expected: GpuWork.SubmissionKinds.Length,
            actual: CountersCeilings.Check(report: Report(run: missing), ceilings: ceilings).Failures.Count);
        var added = run with { Passes = [run.Passes[0] with { Details = ["plain", "stars", "clouds"] }] };

        Assert.Equal(expected: "vulkan: pass=sky detail=clouds node=world has no ceiling recorded",
            actual: Assert.Single(collection: CountersCeilings.Check(report: Report(run: added), ceilings: ceilings).Failures));
    }
    [Fact]
    public void ReportsRequireDetailKeysAndCeilingsRequireLayoutObjects() {
        var report = Report(run: Run(counts: [Count(detail: null, value: 1)]));
        var json = JsonNode.Parse(json: JsonSerializer.Serialize(value: report, jsonTypeInfo: WorldJsonContext.Default.WorldCountersReport))!;

        json["runs"]![0]!["counts"]![0]!.AsObject().Remove(propertyName: "detail");
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(json: json.ToJsonString(), jsonTypeInfo: WorldJsonContext.Default.WorldCountersReport));
        json = JsonNode.Parse(json: JsonSerializer.Serialize(value: report, jsonTypeInfo: WorldJsonContext.Default.WorldCountersReport))!;
        json["runs"]![0]!["passes"]![0]!.AsObject().Remove(propertyName: "details");
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(json: json.ToJsonString(), jsonTypeInfo: WorldJsonContext.Default.WorldCountersReport));
        var ceilings = CountersCeilings.Record(report: report);

        json = JsonNode.Parse(json: JsonSerializer.Serialize(value: ceilings, jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings))!;
        json["backends"]![0]!["ceilings"] = new JsonArray();
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(json: json.ToJsonString(), jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings));
    }

    private static GpuDeviceIdentity Device => new(Backend: "vulkan", AdapterName: "GPU", VendorId: 1, DeviceId: 2,
        DriverVersion: "d", DriverVersionRaw: 0, ApiVersion: "a");

    [Fact]
    public void SkippedDetailIdentitiesParticipateInComparison() {
        var left = Run(counts: []) with { Passes = [new WorldCountersPass(Class: WorkClass.Deterministic, Label: "sky", Node: "world", State: GpuPassState.Skipped) { Details = ["plain", "stars"] }] };
        var right = left with { Passes = [left.Passes[0] with { Details = ["plain"] }] };

        Assert.Equal(expected: "vulkan: pass detail node=world pass=sky detail=stars left=present right=absent",
            actual: Assert.Single(collection: CountersComparison.Reports(left: Report(run: left), right: Report(run: right))));
    }

    private static WorldCount Count(string? detail, long value) => new(Source: "gpu", Node: "world", Pass: "sky",
        Kind: GpuWork.SkyHashes.Name, Class: WorkClass.PerBackendDeterministic, Value: value, Detail: detail);
    private static WorldCountersRun Run(IReadOnlyList<WorldCount> counts) => new(Backend: "vulkan", Device: Device,
        Width: 8, Height: 8, Compiler: "test", GcMode: "test", Counts: counts,
        Passes: [new WorldCountersPass(Class: WorkClass.Deterministic, Label: "sky", Node: "world", State: GpuPassState.Executed) { Details = ["plain", "stars", "clouds"] }]);
    private static WorldCountersReport Report(WorldCountersRun run) => new(Revision: new WorldCountersRevision(Commit: "test", SourceState: "test"),
        Workload: "world.json", Script: "script.txt", Runs: [run]);
}
