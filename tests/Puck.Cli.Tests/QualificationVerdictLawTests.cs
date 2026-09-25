using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Cli.Qualification;
using Puck.World;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>puck qualify</c> judges a matrix cell from its readings alone. A cell passes only when every
/// armed wait is reached, no <c>gpu.created.*</c> count rises across a soak window, every settled inspection shows the
/// instance owning exactly its installed graph, the peak owned or planned pipeline bytes stay within the cell's
/// threshold (equal passes, one byte over fails), every unload releases the instance, every <c>world.reload</c> answers
/// that it applied, no submission is refused at the wire codec, and, with the validation layer on, no validation message
/// appears. A leg that never ran or whose machine lacks the backend is blocked; an absent shader
/// compiler blocks a cell whose profile lets the World find one and fails a cell whose profile withholds it. One failure
/// fails a run, otherwise one blocked check blocks it. The transcript reader recognizes each line the World prints for
/// these readings, and the package check tells a ReadyToRun entry assembly from an IL-only one.
/// </summary>
public sealed class QualificationVerdictLawTests {
    private const long Limit = 1000L;

    private static readonly QualificationCell Cell = new(
        Backend: "vulkan",
        Resolution: new QualificationResolution(Height: 32, Width: 64),
        Threshold: new QualificationThreshold(Backend: "vulkan", Height: 32, PeakDeviceLocalBytes: null, PeakOwnedPipelineBytes: Limit, Width: 64, Workload: "ink"),
        Workload: new QualificationWorkload(
            Name: "ink",
            Pipeline: new QualificationPipeline(Instance: "ink", Layout: "pipeline", Loads: 1, Reloads: 0, Resizes: 0, SettleFrames: 4),
            SoakTicks: 60,
            TimeoutSeconds: 60,
            WarmupTicks: 30,
            World: "Assets/worlds/pipeline.world.json",
            WorldReloads: 0
        )
    );
    private static readonly QualificationExpectation Expected = new(
        ArmedWaits: 2,
        CountersReadings: 2,
        Inspections: 2,
        Releases: 1,
        SoakWindows: [(0, 1)],
        WorldReloads: 2
    );

    private static WorldCountersRun Counters(long pipelines, long images = 3L, long deviceLocalPeak = 0L) => new(
        Backend: "vulkan",
        Compiler: "toolchain",
        Counts: [
            new WorldCount(Class: WorkClass.Pacing, Kind: GpuDeviceMemoryWork.Peak.Name, Node: null, Pass: null, Source: "memory.vulkan", Value: deviceLocalPeak),
            new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: "gpu.created.pipelines", Node: "ink", Pass: null, Source: "gpu", Value: pipelines),
            new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: "gpu.created.images", Node: "ink", Pass: null, Source: "gpu", Value: images),
            new WorldCount(Class: WorkClass.Pacing, Kind: "gpu.pipeline-cache.hits", Node: null, Pass: null, Source: "pipeline-cache.vulkan", Value: (pipelines * 7L)),
            new WorldCount(Class: WorkClass.Deterministic, Kind: "gpu.dispatches", Node: "ink", Pass: "simulation", Source: "gpu", Value: 1L),
        ],
        Device: new GpuDeviceIdentity(AdapterName: "Example GPU", ApiVersion: "1.4.303", Backend: "vulkan", DeviceId: 1U, DriverVersion: "1.0", DriverVersionRaw: 1UL, VendorId: 2U),
        GcMode: "workstation, concurrent",
        Height: 32,
        Passes: [],
        Width: 64
    );
    private static QualificationReadings Good() => new(
        CandidateRefusals: [],
        CompilerAbsent: null,
        Counters: [Counters(pipelines: 2L), Counters(pipelines: 2L)],
        CountersRefusals: [],
        Inspections: [new QualificationInspection(Budget: 4096L, Owned: 400L, Peak: 800L, Steady: 400L), new QualificationInspection(Budget: 4096L, Owned: 500L, Peak: 1000L, Steady: 500L)],
        MemoryProfile: "memory: unified.coherent=no device-local=8589934592",
        Releases: 1,
        SubmissionRefusals: [],
        ValidationMessages: [],
        Waits: [new QualificationWait(Outcome: "reached", Phase: "installed"), new QualificationWait(Outcome: "reached", Phase: "counted 4")],
        WorldReloads: 2
    );
    private static QualificationVerdict Judge(QualificationReadings? readings, WorldOffscreenLegStatus leg = WorldOffscreenLegStatus.Completed, bool debugLayers = true, ReleaseCompilerDiscovery compiler = ReleaseCompilerDiscovery.None) =>
        QualificationJudge.Judge(
            cell: Cell,
            compiler: compiler,
            debugLayers: debugLayers,
            expectation: Expected,
            leg: leg,
            legDetail: ((leg == WorldOffscreenLegStatus.Completed) ? string.Empty : "the leg's detail"),
            readings: readings
        );
    private static void Fails(QualificationReadings readings, string finding) {
        var verdict = Judge(readings: readings);

        Assert.Equal(actual: verdict.Outcome, expected: QualificationOutcome.Fail);
        Assert.Contains(collection: verdict.Findings, filter: line => line.Contains(comparisonType: StringComparison.Ordinal, value: finding));
    }

    [Fact]
    public void ACellWhoseEveryCheckHoldsPasses() {
        var verdict = Judge(readings: Good());

        Assert.Equal(actual: verdict.Outcome, expected: QualificationOutcome.Pass);
        Assert.Empty(collection: verdict.Findings);
        Assert.Equal(actual: Good().PeakOwnedPipelineBytes, expected: Limit);
    }
    [Fact]
    public void ThePeakThresholdIsInclusive() {
        var readings = Good();

        Assert.Equal(actual: Judge(readings: readings).Outcome, expected: QualificationOutcome.Pass);
        Fails(
            finding: "over the cell's threshold of 1000",
            readings: (readings with { Inspections = [.. readings.Inspections, new QualificationInspection(Budget: 4096L, Owned: 500L, Peak: (Limit + 1L), Steady: 500L)] })
        );
    }
    [Fact]
    public void ADeviceLocalThresholdJudgesTheLargestPeakInclusively() {
        const long DeviceLocalLimit = 4096L;

        var cell = (Cell with { Threshold = (Cell.Threshold with { PeakDeviceLocalBytes = DeviceLocalLimit }) });
        QualificationVerdict JudgeCell(QualificationReadings readings) => QualificationJudge.Judge(
            cell: cell,
            compiler: ReleaseCompilerDiscovery.None,
            debugLayers: true,
            expectation: Expected,
            leg: WorldOffscreenLegStatus.Completed,
            legDetail: string.Empty,
            readings: readings
        );
        var atLimit = (Good() with { Counters = [Counters(deviceLocalPeak: 1024L, pipelines: 2L), Counters(deviceLocalPeak: DeviceLocalLimit, pipelines: 2L)] });
        var overLimit = (Good() with { Counters = [Counters(deviceLocalPeak: (DeviceLocalLimit + 1L), pipelines: 2L), Counters(deviceLocalPeak: 1024L, pipelines: 2L)] });
        var unread = (Good() with { Counters = [Counters(pipelines: 2L) with { Counts = [] }, Counters(pipelines: 2L) with { Counts = [] }] });

        Assert.Equal(actual: atLimit.PeakDeviceLocalBytes, expected: DeviceLocalLimit);
        Assert.Equal(actual: JudgeCell(readings: atLimit).Outcome, expected: QualificationOutcome.Pass);
        Assert.Contains(collection: JudgeCell(readings: overLimit).Findings, filter: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: "4097 device-local bytes at its peak, over the cell's threshold of 4096"));
        Assert.Contains(collection: JudgeCell(readings: unread).Findings, filter: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: "no world.counters reading reported gpu.memory.device-local.peak"));
        Assert.Equal(actual: Judge(readings: overLimit).Outcome, expected: QualificationOutcome.Pass);
    }
    [Fact]
    public void AnObjectCreatedAcrossASoakFails() {
        Fails(
            finding: "gpu/ink gpu.created.pipelines 2 -> 3",
            readings: (Good() with { Counters = [Counters(pipelines: 2L), Counters(pipelines: 3L)] })
        );
        Assert.Equal(
            actual: Judge(readings: (Good() with { Counters = [Counters(pipelines: 2L), Counters(pipelines: 2L)] })).Outcome,
            expected: QualificationOutcome.Pass
        );
    }
    [Fact]
    public void ASettledInstanceOwningMoreThanItsGraphFails() =>
        Fails(
            finding: "owns 600 bytes, not exactly its installed graph's 500",
            readings: (Good() with { Inspections = [Good().Inspections[0], new QualificationInspection(Budget: 4096L, Owned: 600L, Peak: 900L, Steady: 500L)] })
        );
    [Fact]
    public void EveryExpectationIsCounted() {
        Fails(finding: "0 unload(s) released", readings: (Good() with { Releases = 0 }));
        Fails(finding: "1 pipeline.inspect response(s)", readings: (Good() with { Inspections = [Good().Inspections[0]] }));
        Fails(finding: "1 world.counters reading(s)", readings: (Good() with { Counters = [Counters(pipelines: 2L)] }));
        Fails(finding: "1 pipeline.wait outcome(s)", readings: (Good() with { Waits = [Good().Waits[0]] }));
        Fails(finding: "pipeline.wait counted 4: timed out", readings: (Good() with { Waits = [Good().Waits[0], new QualificationWait(Outcome: "timed out", Phase: "counted 4")] }));
        Fails(finding: "refused: kind", readings: (Good() with { CountersRefusals = ["kind 'x' is not in the legend"] }));
        Fails(finding: "GPU candidate refused", readings: (Good() with { CandidateRefusals = ["[pipeline: ink GPU candidate refused: SHADERPIPE_BUDGET"] }));
    }
    [Fact]
    public void AWorldReloadThatDoesNotApplyFails() {
        const string Refused = "[world.codec refused: PayloadMalformed: the embedded world definition is not a valid puck.world.definition.v1 document]";

        Fails(finding: "1 world.reload(s) applied, of the 2 the script makes", readings: (Good() with { WorldReloads = 1 }));
        Fails(finding: Refused, readings: (Good() with { SubmissionRefusals = [Refused] }));
        Assert.Equal(actual: Judge(debugLayers: false, readings: (Good() with { WorldReloads = 1, SubmissionRefusals = [Refused] })).Outcome, expected: QualificationOutcome.Fail);
    }
    [Fact]
    public void AValidationMessageFailsOnlyWithTheLayerOn() {
        var readings = (Good() with { ValidationMessages = ["[d3d12-debug] D3D12_MESSAGE_SEVERITY_ERROR: before state mismatch"] });

        Fails(finding: "1 validation-layer message(s)", readings: readings);
        Assert.Equal(actual: Judge(debugLayers: false, readings: readings).Outcome, expected: QualificationOutcome.Pass);
    }
    [Fact]
    public void ALegThatCouldNotRunHereIsBlockedAndOneThatMisbehavedFails() {
        Assert.Equal(actual: Judge(leg: WorldOffscreenLegStatus.Unsupported, readings: Good()).Outcome, expected: QualificationOutcome.Blocked);
        Assert.Equal(actual: Judge(leg: WorldOffscreenLegStatus.NotRun, readings: null).Outcome, expected: QualificationOutcome.Blocked);

        foreach (var leg in ((WorldOffscreenLegStatus[])[WorldOffscreenLegStatus.Exited, WorldOffscreenLegStatus.TimedOut, WorldOffscreenLegStatus.CommandsRejected])) {
            var verdict = Judge(leg: leg, readings: Good());

            Assert.Equal(actual: verdict.Outcome, expected: QualificationOutcome.Fail);
            Assert.Contains(expected: "the leg's detail", collection: verdict.Findings);
        }
    }
    [Fact]
    public void AnAbsentCompilerBlocksUnderPathAndFailsUnderNone() {
        var readings = (Good() with { CompilerAbsent = "[pipeline: ink unsupported: dxc is not on the path]" });

        Assert.Equal(actual: Judge(compiler: ReleaseCompilerDiscovery.Path, readings: readings).Outcome, expected: QualificationOutcome.Blocked);

        var withheld = Judge(compiler: ReleaseCompilerDiscovery.None, readings: readings);

        Assert.Equal(actual: withheld.Outcome, expected: QualificationOutcome.Fail);
        Assert.Contains(collection: withheld.Findings, filter: static finding => finding.Contains(comparisonType: StringComparison.Ordinal, value: "compiler policy None"));
        Assert.Equal(
            actual: Judge(compiler: ReleaseCompilerDiscovery.Path, readings: (Good() with { Waits = [Good().Waits[0], new QualificationWait(Outcome: "unsupported", Phase: "compiled")] })).Outcome,
            expected: QualificationOutcome.Blocked
        );
    }
    [Fact]
    public void AFailureOutranksABlockedCheckWhichOutranksAPass() {
        Assert.Equal(actual: QualifyCommand.Overall(cells: [QualificationOutcome.Pass, QualificationOutcome.Blocked, QualificationOutcome.Fail]), expected: QualificationOutcome.Fail);
        Assert.Equal(actual: QualifyCommand.Overall(cells: [QualificationOutcome.Pass, QualificationOutcome.Blocked]), expected: QualificationOutcome.Blocked);
        Assert.Equal(actual: QualifyCommand.Overall(cells: [QualificationOutcome.Pass, QualificationOutcome.Pass]), expected: QualificationOutcome.Pass);
    }
    [Fact]
    public void TheReaderRecognizesEveryLineTheWorldPrints() {
        const string Reading = """[world.counters: {"sources":[],"gpu":{"device":{"backend":"vulkan","adapter":"Example GPU","vendor":2,"device":1,"driver":"1.0","driver.raw":1,"api":"1.4.303","driver.name":"","driver.id":0,"conformance":""},"nodes":[{"name":"ink","sample":null,"lifetime":{"gpu.created.pipelines":2}}]},"allocation":{"gcMode":"workstation, concurrent","windows":{}},"kinds":{"gpu.created.pipelines":{"unit":"count","class":"per-backend-deterministic"}}}]""";

        static CliProcessOutputLine Out(string line, CliProcessOutputStream stream = CliProcessOutputStream.Stdout) => new(ElapsedMilliseconds: 0d, Line: line, Sequence: 0L, Stream: stream);
        var process = new CliProcessResult(
            ExitCode: 0,
            OutputLines: [
                Out(line: Reading),
                Out(line: "[pipeline.inspect: ink; owned=400 bytes; steady=400 bytes; peak=800 bytes; budget=4096 bytes; submission=12"),
                Out(line: "  memory: unified.coherent=no device-local=8589934592 device-local.largest-heap=8589934592 device-local.host-visible=0"),
                Out(line: "[pipeline: ink wait counted 4 reached after 5 frame(s)]", stream: CliProcessOutputStream.Stderr),
                Out(line: "[pipeline.inspect: 'ink' has no rendered pipeline instance]", stream: CliProcessOutputStream.Stderr),
                Out(line: "[vulkan-debug] validation error: an object was not destroyed", stream: CliProcessOutputStream.Stderr),
                Out(line: "[vulkan-debug] general: loader notice", stream: CliProcessOutputStream.Stderr),
                Out(line: "[pipeline: ink GPU candidate refused: SHADERPIPE_BUDGET", stream: CliProcessOutputStream.Stderr),
                Out(line: "[pipeline: ink unsupported: the dxc shader tool is absent", stream: CliProcessOutputStream.Stderr),
                Out(line: "[world.reload: world.reload applied — base is 'Assets/worlds/pipeline.world.json' (world.reload), journal cleared]"),
                Out(line: "[world.definition: world.reload applied — base is 'Assets/worlds/pipeline.world.json' (world.reload), journal cleared]", stream: CliProcessOutputStream.Stderr),
                Out(line: "[world.codec refused: PayloadMalformed: a cell holds no value]", stream: CliProcessOutputStream.Stderr),
                Out(line: "[world.reload: the file is missing]", stream: CliProcessOutputStream.Stderr),
                Out(line: Reading),
            ],
            Stderr: string.Empty,
            Stdout: string.Empty,
            TimedOut: false
        );
        var readings = QualificationJudge.Read(cell: Cell, compiler: "toolchain", process: process);

        Assert.Equal(actual: readings.Counters.Count, expected: 2);
        Assert.Empty(collection: readings.CountersRefusals);
        Assert.Equal(actual: readings.Inspections, expected: [new QualificationInspection(Budget: 4096L, Owned: 400L, Peak: 800L, Steady: 400L)]);
        Assert.StartsWith(actualString: readings.MemoryProfile, expectedStartString: "memory: unified.coherent=no device-local=8589934592");
        Assert.Equal(actual: readings.Waits, expected: [new QualificationWait(Outcome: "reached", Phase: "counted 4")]);
        Assert.Equal(actual: readings.Releases, expected: 1);
        Assert.Equal(actual: readings.ValidationMessages, expected: ["[vulkan-debug] validation error: an object was not destroyed"]);
        Assert.Single(collection: readings.CandidateRefusals);
        Assert.Equal(actual: readings.CompilerAbsent, expected: "[pipeline: ink unsupported: the dxc shader tool is absent");
        Assert.Equal(actual: readings.WorldReloads, expected: 1);
        Assert.Equal(actual: readings.SubmissionRefusals, expected: ["[world.codec refused: PayloadMalformed: a cell holds no value]", "[world.reload: the file is missing]"]);
    }
    [Fact]
    public void ThePackageCheckTellsReadyToRunFromIlOnly() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-qualify-law-");
        var publish = new ReleasePublish(Compiler: ReleaseCompilerDiscovery.None, EntryAssembly: "Puck.World.dll", Mode: ReleasePublishMode.ReadyToRun);
        var entry = Path.Combine(path1: directory.FullName, path2: publish.EntryAssembly);

        try {
            Assert.False(condition: QualificationPackage.TryVerify(directory: directory.FullName, entry: out _, publish: publish, reason: out var missing));
            Assert.Contains(actualString: missing, expectedSubstring: "no entry assembly");

            File.Copy(destFileName: entry, sourceFileName: typeof(QualificationVerdictLawTests).Assembly.Location);
            Assert.False(condition: QualificationPackage.TryVerify(directory: directory.FullName, entry: out _, publish: publish, reason: out var ilOnly));
            Assert.Contains(actualString: ilOnly, expectedSubstring: "no ReadyToRun header");

            // The shared framework ships precompiled, so its core library carries a ReadyToRun header.
            File.Copy(destFileName: entry, overwrite: true, sourceFileName: typeof(object).Assembly.Location);
            Assert.True(condition: QualificationPackage.TryVerify(directory: directory.FullName, entry: out var verified, publish: publish, reason: out var reason), userMessage: reason);
            Assert.Equal(actual: verified, expected: Path.GetFullPath(path: entry));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void TheLegWithholdsTheCompilerAndFlagsOnlyTheListedLayers() {
        var separator = Path.PathSeparator;
        var withheld = QualificationPackage.LegEnvironment(
            compiler: ReleaseCompilerDiscovery.None,
            holdsCompiler: static directory => directory.EndsWith(comparisonType: StringComparison.Ordinal, value: "dxc-bin"),
            searchPath: $"C:/tools{separator}C:/dxc-bin{separator}C:/other"
        );

        Assert.Equal(actual: withheld["PATH"], expected: $"C:/tools{separator}C:/other");
        Assert.Empty(collection: QualificationPackage.LegEnvironment(compiler: ReleaseCompilerDiscovery.Path, holdsCompiler: static _ => true, searchPath: "C:/dxc-bin"));

        var profile = new ReleaseProfile(
            Backends: ["vulkan", "directx"],
            DebugLayers: ["directx"],
            Deferred: [],
            Functional: [],
            Publish: new ReleasePublish(Compiler: ReleaseCompilerDiscovery.None, EntryAssembly: "Puck.World.dll", Mode: ReleasePublishMode.ReadyToRun),
            Resolutions: [],
            Schema: ReleaseProfile.SchemaVersion,
            Thresholds: [],
            Workloads: []
        );

        Assert.Equal(actual: QualificationPackage.DebugLayerArguments(backend: "directx", profile: profile), expected: ["--debug-layers"]);
        Assert.Empty(collection: QualificationPackage.DebugLayerArguments(backend: "vulkan", profile: profile));
    }
}
