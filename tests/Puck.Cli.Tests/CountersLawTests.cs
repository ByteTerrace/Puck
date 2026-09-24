using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Cli.Counters;
using Puck.World;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>puck counters</c> tags every count it reads from <c>world.counters --json</c> with the class
/// the World's legend declares (a GPU node's submission and revision are pacing), refuses a reading it cannot trust,
/// and <c>puck counters compare</c> holds two reports to each other by class — exit 0 when every comparable count
/// agrees, exit 1 naming the kind, pass and node of each difference, pacing never compared, an allocation reading
/// compared only as zero or not zero, and exit 2 for a file that is not a report.
/// </summary>
public sealed class CountersLawTests {
    private const string Reading = """
        {"sources":[{"name":"state.arena","counts":{"state.arena.visits":12}}],
         "gpu":{"device":{"backend":"vulkan","adapter":"Example GPU","vendor":4318,"device":10118,"driver":"566.36","driver.raw":2374860800,"api":"1.4.303","driver.name":"","driver.id":0,"conformance":""},
                "nodes":[{"name":"world","sample":{"submission":61,"revision":3,"passes":[{"label":"upload","state":"executed","counts":{"gpu.dispatches":1}},{"label":"sky","state":"skipped"}],"outside":{"gpu.command-buffers":1}},"lifetime":{"gpu.created.pipelines":14}}]},
         "allocation":{"gcMode":"workstation, concurrent","windows":{"world.counters.read":0}},
         "kinds":{"state.arena.visits":{"unit":"lanes","class":"deterministic"},"gpu.dispatches":{"unit":"count","class":"deterministic"},"gpu.command-buffers":{"unit":"count","class":"deterministic"},"gpu.created.pipelines":{"unit":"count","class":"per-backend-deterministic"}}}
        """;

    private static GpuDeviceIdentity Device(string backend) => new(
        AdapterName: "Example GPU",
        ApiVersion: "1.4.303",
        Backend: backend,
        DeviceId: 0x2786U,
        DriverVersion: "566.36",
        DriverVersionRaw: 0x8D8D8000UL,
        VendorId: 0x10DEU
    );
    private static WorldCountersRun Run(string backend, long dispatches = 1L, long submission = 61L, long pipelines = 14L, long allocated = 0L, GpuPassState sky = GpuPassState.Skipped) => new(
        Backend: backend,
        Compiler: "toolchain",
        Counts: [
            new WorldCount(Class: WorkClass.Deterministic, Kind: "state.arena.visits", Node: null, Pass: null, Source: "state.arena", Value: 12L),
            new WorldCount(Class: WorkClass.Pacing, Kind: CountersReading.SubmissionKind, Node: "world", Pass: null, Source: "gpu", Value: submission),
            new WorldCount(Class: WorkClass.Deterministic, Kind: "gpu.dispatches", Node: "world", Pass: "upload", Source: "gpu", Value: dispatches),
            new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: "gpu.created.pipelines", Node: "world", Pass: null, Source: "gpu", Value: pipelines),
            new WorldCount(Class: WorkClass.AllocationZeroNonzero, Kind: "world.counters.read", Node: null, Pass: null, Source: "allocation", Value: allocated),
        ],
        Device: Device(backend: backend),
        GcMode: "workstation, concurrent",
        Height: 144,
        Passes: [
            new WorldCountersPass(Label: "upload", Node: "world", State: GpuPassState.Executed),
            new WorldCountersPass(Label: "sky", Node: "world", State: sky),
        ],
        Width: 256
    );
    private static WorldCountersReport Report(WorldCountersRun vulkan) => new(
        Revision: new WorldCountersRevision(Commit: "0123abcd", SourceState: "state-key"),
        Runs: [vulkan, Run(backend: "directx")],
        Script: CountersCommand.ScriptPath,
        Workload: CountersCommand.WorldPath
    );
    private static (int ExitCode, string Output, string Error) Compare(WorldCountersReport left, WorldCountersReport right) {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-counters-law-");

        try {
            var leftPath = Path.Combine(path1: directory.FullName, path2: "left.json");
            var rightPath = Path.Combine(path1: directory.FullName, path2: "right.json");

            CountersCommand.WriteReport(path: leftPath, report: left);
            CountersCommand.WriteReport(path: rightPath, report: right);

            return ConsoleCapture.RunSplit(run: () => CountersCommand.Compare(left: leftPath, right: rightPath));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    private static (int ExitCode, string Output, string Error) CompareText(string leftText) {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-counters-law-");

        try {
            var leftPath = Path.Combine(path1: directory.FullName, path2: "left.json");
            var rightPath = Path.Combine(path1: directory.FullName, path2: "right.json");

            File.WriteAllText(contents: leftText, path: leftPath);
            CountersCommand.WriteReport(path: rightPath, report: Report(vulkan: Run(backend: "vulkan")));

            return ConsoleCapture.RunSplit(run: () => CountersCommand.Compare(left: leftPath, right: rightPath));
        } finally {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AReadingTagsEveryCountWithItsClass() {
        using var document = JsonDocument.Parse(json: Reading);

        Assert.True(condition: CountersReading.TryRead(
            backend: "vulkan",
            compiler: "toolchain",
            height: 144,
            reading: document.RootElement,
            reason: out var reason,
            run: out var run,
            width: 256
        ), userMessage: reason);

        string Class(string kind, string? pass = null) =>
            run.Counts.Single(predicate: count => ((count.Kind == kind) && (count.Pass == pass))).Class switch {
                WorkClass.Deterministic => "deterministic",
                WorkClass.PerBackendDeterministic => "per-backend-deterministic",
                WorkClass.Pacing => "pacing",
                _ => "allocation-zero-nonzero",
            };

        Assert.Equal(actual: Class(kind: "state.arena.visits"), expected: "deterministic");
        Assert.Equal(actual: Class(kind: "gpu.dispatches", pass: "upload"), expected: "deterministic");
        Assert.Equal(actual: Class(kind: "gpu.command-buffers"), expected: "deterministic");
        Assert.Equal(actual: Class(kind: "gpu.created.pipelines"), expected: "per-backend-deterministic");
        Assert.Equal(actual: Class(kind: CountersReading.SubmissionKind), expected: "pacing");
        Assert.Equal(actual: Class(kind: CountersReading.RevisionKind), expected: "pacing");
        Assert.Equal(actual: Class(kind: "world.counters.read"), expected: "allocation-zero-nonzero");
        Assert.Equal(actual: run.GcMode, expected: "workstation, concurrent");
        Assert.Equal(actual: run.Device, expected: Device(backend: "vulkan"));
        Assert.Equal(
            actual: run.Passes.Select(selector: static pass => $"{pass.Node}/{pass.Label}/{pass.State}"),
            expected: ["world/upload/Executed", "world/sky/Skipped"]
        );
    }
    [Fact]
    public void AReadingItCannotTrustIsRefused() {
        static string Refusal(string reading, string backend = "vulkan") {
            using var document = JsonDocument.Parse(json: reading);

            Assert.False(condition: CountersReading.TryRead(
                backend: backend,
                compiler: "toolchain",
                height: 144,
                reading: document.RootElement,
                reason: out var reason,
                run: out _,
                width: 256
            ));

            return reason;
        }

        Assert.Contains(expectedSubstring: "device reports vulkan", actualString: Refusal(backend: "directx", reading: Reading));
        Assert.Contains(expectedSubstring: "not in the reading's kinds legend", actualString: Refusal(reading: Reading.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"state.arena.other\":{\"unit\"", oldValue: "\"state.arena.visits\":{\"unit\"")));
        Assert.Contains(expectedSubstring: "unknown class", actualString: Refusal(reading: Reading.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"class\":\"PerBackend\"", oldValue: "\"class\":\"per-backend-deterministic\"")));
        Assert.Contains(expectedSubstring: "device is unavailable", actualString: Refusal(reading: """{"sources":[],"gpu":{"device":null,"nodes":[]},"allocation":{"gcMode":"x","windows":{}},"kinds":{}}"""));
        Assert.Contains(expectedSubstring: "no gpu section", actualString: Refusal(reading: """{"sources":[],"allocation":{"gcMode":"x","windows":{}},"kinds":{}}"""));
    }
    [Fact]
    public void OnlyTheOneResponseInALegsOutputIsRead() {
        var line = $"[world.counters: {Reading.ReplaceLineEndings(replacementText: string.Empty)}]";

        Assert.True(condition: CountersReading.TryRead(backend: "vulkan", compiler: "toolchain", height: 144, reason: out var reason, run: out _, stdout: ["[world.cadence: off]", line, "[wire.errors: 0 rejected]"], width: 256), userMessage: reason);
        Assert.False(condition: CountersReading.TryRead(backend: "vulkan", compiler: "toolchain", height: 144, reason: out reason, run: out _, stdout: [line, line], width: 256));
        Assert.Contains(actualString: reason, expectedSubstring: "found 2");
        Assert.False(condition: CountersReading.TryRead(backend: "vulkan", compiler: "toolchain", height: 144, reason: out reason, run: out _, stdout: [], width: 256));
    }
    [Fact]
    public void TheDeviceIsSpelledAsWorldCountersWritesIt() {
        var identity = Device(backend: "directx") with { ConformanceVersion = "1.4.1.0", DriverId = 4U, DriverName = "Example driver" };
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: buffer)) {
            identity.WriteJson(writer: writer);
        }

        Assert.Equal(
            actual: JsonSerializer.Deserialize(jsonTypeInfo: WorldJsonContext.Default.GpuDeviceIdentity, utf8Json: buffer.ToArray()),
            expected: identity
        );
    }
    [Fact]
    public void AcrossBackendsOnlyDeterministicCountsAndPassStatesMustAgree() {
        Assert.Empty(collection: CountersComparison.AcrossBackends(left: Run(backend: "vulkan"), right: Run(backend: "directx", pipelines: 15L, submission: 90L, allocated: 64L)));

        var differences = CountersComparison.AcrossBackends(left: Run(backend: "vulkan"), right: Run(backend: "directx", dispatches: 2L, sky: GpuPassState.Executed));

        Assert.Equal(
            actual: differences,
            expected: [
                "deterministic kind=gpu.dispatches pass=upload node=world source=gpu vulkan=1 directx=2",
                "pass state node=world pass=sky vulkan=skipped directx=executed",
            ]
        );
    }
    [Fact]
    public void EqualReportsCompareClean() {
        var (exitCode, output, _) = Compare(left: Report(vulkan: Run(backend: "vulkan")), right: Report(vulkan: Run(backend: "vulkan")));

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Equal(actual: output, expected: string.Empty);
    }
    [Fact]
    public void OneDeterministicDifferenceFailsNamingKindPassAndNode() {
        var (exitCode, output, _) = Compare(left: Report(vulkan: Run(backend: "vulkan")), right: Report(vulkan: Run(backend: "vulkan", dispatches: 3L)));

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Equal(actual: output.ReplaceLineEndings(replacementText: "\n"), expected: "counters: vulkan: deterministic kind=gpu.dispatches pass=upload node=world source=gpu left=1 right=3\n");
    }
    [Fact]
    public void APerBackendCountIsHeldToItsOwnBackendAcrossReports() {
        var (exitCode, output, _) = Compare(left: Report(vulkan: Run(backend: "vulkan")), right: Report(vulkan: Run(backend: "vulkan", pipelines: 15L)));

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: output, expectedSubstring: "per-backend-deterministic kind=gpu.created.pipelines pass=- node=world");
    }
    [Fact]
    public void APacingOnlyDifferenceIsIgnored() {
        var (exitCode, output, _) = Compare(left: Report(vulkan: Run(backend: "vulkan")), right: Report(vulkan: Run(backend: "vulkan", submission: 999L)));

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Equal(actual: output, expected: string.Empty);
    }
    [Fact]
    public void AnAllocationReadingComparesOnlyAsZeroOrNotZero() {
        var (flipped, flippedOutput, _) = Compare(left: Report(vulkan: Run(backend: "vulkan")), right: Report(vulkan: Run(backend: "vulkan", allocated: 24L)));

        Assert.Equal(actual: flipped, expected: 1);
        Assert.Contains(actualString: flippedOutput, expectedSubstring: "vulkan: allocation-zero-nonzero kind=world.counters.read pass=- node=- source=allocation left=0 right=24");

        var (resized, resizedOutput, _) = Compare(left: Report(vulkan: Run(backend: "vulkan", allocated: 24L)), right: Report(vulkan: Run(backend: "vulkan", allocated: 4096L)));

        Assert.Equal(actual: resized, expected: 0);
        Assert.Equal(actual: resizedOutput, expected: string.Empty);
    }
    [Fact]
    public void APassStateThatMovedIsADifference() {
        var (exitCode, output, _) = Compare(left: Report(vulkan: Run(backend: "vulkan")), right: Report(vulkan: Run(backend: "vulkan", sky: GpuPassState.NotReached)));

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: output, expectedSubstring: "vulkan: pass state node=world pass=sky left=skipped right=not-reached");
    }
    [Fact]
    public void AFileThatIsNotAReportIsRefused() {
        var written = JsonSerializer.Serialize(value: Report(vulkan: Run(backend: "vulkan")), jsonTypeInfo: WorldJsonContext.Default.WorldCountersReport);

        var (foreign, _, foreignError) = CompareText(leftText: written.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"puck.parity.manifest.v1\"", oldValue: $"\"{WorldCountersReport.SchemaVersion}\""));

        Assert.Equal(actual: foreign, expected: 2);
        Assert.Contains(actualString: foreignError, expectedSubstring: "a foreign report");

        var (unmapped, _, unmappedError) = CompareText(leftText: written.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"extra\":1,\"runs\":", oldValue: "\"runs\":"));

        Assert.Equal(actual: unmapped, expected: 2);
        Assert.Contains(actualString: unmappedError, expectedSubstring: "not a puck.counters.report.v1 report");

        var (garbage, _, _) = CompareText(leftText: "not json");

        Assert.Equal(actual: garbage, expected: 2);
    }
    [Fact]
    public void AWrittenReportReadsBackAsWritten() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-counters-law-");

        try {
            var path = Path.Combine(path1: directory.FullName, path2: "report.json");
            var report = Report(vulkan: Run(backend: "vulkan"));

            CountersCommand.WriteReport(path: path, report: report);

            Assert.True(condition: CountersCommand.TryReadReport(path: path, reason: out var reason, report: out var read), userMessage: reason);
            Assert.Equal(
                actual: JsonSerializer.Serialize(value: read, jsonTypeInfo: WorldJsonContext.Default.WorldCountersReport),
                expected: JsonSerializer.Serialize(value: report, jsonTypeInfo: WorldJsonContext.Default.WorldCountersReport)
            );
            Assert.Contains(expectedSubstring: "\"class\": \"per-backend-deterministic\"", actualString: File.ReadAllText(path: path, encoding: Encoding.UTF8));
        } finally {
            directory.Delete(recursive: true);
        }
    }
}
