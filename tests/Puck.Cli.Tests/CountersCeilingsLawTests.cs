using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Cli.Counters;
using Puck.World;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>puck counters --record</c> writes every GPU submission count of a render node that is
/// deterministic or per-backend-deterministic, pass by pass and outside every pass, as its own ceiling, so a count that
/// read zero is a required zero, and every kind of a pass that did not execute at a required zero; and
/// <c>puck counters --check</c> fails a count raised over its ceiling and a required zero broken, naming the backend,
/// class, kind, pass and node, fails a recorded expectation the run did not measure or measured as another class, fails a
/// count no ceiling was recorded for, and judges a
/// per-backend-deterministic count only on the device its backend's ceilings were recorded on, reporting it as not judged
/// on any other, except a required zero: a zero of a kernel kind (a magnitude that follows the device), recorded as such,
/// is a structural contract that holds on every device and is judged on every one, and the report says how many zeros it
/// still judged.
/// </summary>
public sealed class CountersCeilingsLawTests {
    private static GpuDeviceIdentity Device(string backend, string driver = "566.36") => new(
        AdapterName: "Example GPU",
        ApiVersion: "1.4.303",
        Backend: backend,
        DeviceId: 0x1F08U,
        DriverVersion: driver,
        DriverVersionRaw: 0x8D8D8000UL,
        VendorId: 0x10DEU
    );
    // One backend's run of the view's primary march, its cull arguments and the ambient pass the tier skips, the work outside
    // its passes, and counts no ceiling holds: a source's, a pacing identity and a created object.
    private static WorldCountersRun Run(string backend, long steps = 4096L, long cullSteps = 0L, long ambientTexels = 0L, long dispatches = 1L, GpuDeviceIdentity? device = null) => new(
        Backend: backend,
        Compiler: "toolchain",
        Counts: [
            new WorldCount(Class: WorkClass.Deterministic, Kind: "state.arena.visits", Node: null, Pass: null, Source: "state.arena", Value: 12L),
            new WorldCount(Class: WorkClass.Pacing, Kind: CountersReading.SubmissionKind, Node: "world", Pass: null, Source: "gpu", Value: 61L),
            new WorldCount(Class: WorkClass.Deterministic, Kind: GpuWork.IndirectDispatches.Name, Node: "world", Pass: "sdf.world$primary", Source: "gpu", Value: dispatches),
            new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.MarchSteps.Name, Node: "world", Pass: "sdf.world$primary", Source: "gpu", Value: steps),
            new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.MarchSteps.Name, Node: "world", Pass: "sdf.world$cull-args", Source: "gpu", Value: cullSteps),
            new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.TexelsWritten.Name, Node: "world", Pass: "sdf.world$ambient", Source: "gpu", Value: ambientTexels),
            new WorldCount(Class: WorkClass.Deterministic, Kind: GpuWork.Clears.Name, Node: "world", Pass: null, Source: "gpu", Value: 1L),
            new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.PipelinesCreated.Name, Node: "world", Pass: null, Source: "gpu", Value: 14L),
        ],
        Device: (device ?? Device(backend: backend)),
        GcMode: "workstation, concurrent",
        Height: 1080,
        Passes: [],
        Width: 1920
    );
    private static WorldCountersReport Report(WorldCountersRun vulkan, WorldCountersRun? directx = null) => new(
        Revision: new WorldCountersRevision(Commit: "0123abcd", SourceState: "state-key"),
        Runs: [vulkan, (directx ?? Run(backend: "directx"))],
        Script: CountersCommand.ScriptPath,
        Workload: CountersCommand.WorldPath
    );

    private static WorldCountersCeilings Recorded { get; } = CountersCeilings.Record(report: Report(vulkan: Run(backend: "vulkan")));

    private static WorldCountersRun WithUpload(WorldCountersRun run, long dispatches, GpuPassState state = GpuPassState.Executed) => run with {
        Counts = [.. run.Counts, new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.Dispatches.Name, Node: "world", Pass: "upload", Source: "gpu", Value: dispatches)],
        Passes = [.. run.Passes, new WorldCountersPass(Class: WorkClass.PerBackendDeterministic, Label: "upload", Node: "world", State: state)],
    };

    [Fact]
    public void RecordingHoldsEveryGpuSubmissionCountOfANodeAtItsReading() {
        var vulkan = Assert.Single(collection: Recorded.Runs, predicate: static run => (run.Backend == "vulkan"));

        Assert.Equal(actual: Recorded.Schema, expected: WorldCountersCeilings.SchemaVersion);
        Assert.Equal(actual: (Recorded.Workload, Recorded.Script), expected: (CountersCommand.WorldPath, CountersCommand.ScriptPath));
        Assert.Equal(actual: vulkan.Device, expected: Device(backend: "vulkan"));
        Assert.Equal(
            actual: vulkan.Ceilings.Select(selector: static ceiling => (ceiling.Pass, ceiling.Kind, ceiling.Ceiling)),
            expected: [
                ("sdf.world$primary", GpuWork.IndirectDispatches.Name, 1L),
                ("sdf.world$primary", GpuWork.MarchSteps.Name, 4096L),
                ("sdf.world$cull-args", GpuWork.MarchSteps.Name, 0L),
                ("sdf.world$ambient", GpuWork.TexelsWritten.Name, 0L),
                (null, GpuWork.Clears.Name, 1L),
            ]
        );
    }
    // A zero of a kernel kind is a structural contract and is recorded as a required zero; a nonzero reading, a deterministic
    // zero and a deterministic kind's zero loosened to a device-following pass's class are not.
    [Fact]
    public void RecordingMarksAKernelKindsZeroAsARequiredZeroAndNothingElse() {
        var run = WithUpload(dispatches: 0L, run: Run(backend: "vulkan"));
        var vulkan = Assert.Single(collection: CountersCeilings.Record(report: Report(vulkan: run)).Runs, predicate: static run => (run.Backend == "vulkan"));

        Assert.Equal(
            actual: vulkan.Ceilings.Where(predicate: static ceiling => ceiling.RequiredZero).Select(selector: static ceiling => (ceiling.Pass, ceiling.Kind)),
            expected: [
                ("sdf.world$cull-args", GpuWork.MarchSteps.Name),
                ("sdf.world$ambient", GpuWork.TexelsWritten.Name),
            ]
        );
        Assert.All(
            collection: vulkan.Ceilings.Where(predicate: static ceiling => (ceiling.RequiredZero)),
            action: static ceiling => Assert.Equal(actual: (ceiling.Ceiling, ceiling.Class), expected: (0L, WorkClass.PerBackendDeterministic))
        );

        var loosened = Assert.Single(collection: vulkan.Ceilings, predicate: static ceiling => (ceiling.Pass == "upload"));

        Assert.Equal(actual: (loosened.Class, loosened.Ceiling, loosened.RequiredZero), expected: (WorkClass.PerBackendDeterministic, 0L, false));
    }
    [Fact]
    public void TheRecordingRunHoldsItsCeilings() {
        var verdict = CountersCeilings.Check(
            ceilings: Recorded,
            report: Report(vulkan: Run(backend: "vulkan"))
        );

        Assert.Empty(collection: verdict.Failures);
        Assert.Empty(collection: verdict.Notes);
    }
    [Fact]
    public void ACountRaisedOverItsCeilingFailsNamingItsKindPassAndNode() {
        var verdict = CountersCeilings.Check(
            ceilings: Recorded,
            report: Report(vulkan: Run(backend: "vulkan", steps: 4097L))
        );

        Assert.Equal(
            actual: Assert.Single(collection: verdict.Failures),
            expected: "vulkan: per-backend-deterministic kind=gpu.march.steps pass=sdf.world$primary node=world is over its ceiling: reads 4097, ceiling 4096"
        );

        // A count that falls stays within its ceiling.
        Assert.Empty(collection: CountersCeilings.Check(ceilings: Recorded, report: Report(vulkan: Run(backend: "vulkan", steps: 4000L))).Failures);
    }
    [Fact]
    public void ARequiredZeroBrokenFailsNamingItsKindPassAndNode() {
        var verdict = CountersCeilings.Check(
            ceilings: Recorded,
            report: Report(vulkan: Run(ambientTexels: 12L, backend: "vulkan", cullSteps: 3L))
        );

        Assert.Equal(
            actual: verdict.Failures,
            expected: [
                "vulkan: per-backend-deterministic kind=gpu.march.steps pass=sdf.world$cull-args node=world breaks its required zero: reads 3",
                "vulkan: per-backend-deterministic kind=gpu.texels.written pass=sdf.world$ambient node=world breaks its required zero: reads 12",
            ]
        );
    }
    [Fact]
    public void ACountNoCeilingHoldsFails() {
        var run = Run(backend: "vulkan");
        var verdict = CountersCeilings.Check(
            ceilings: Recorded,
            report: Report(vulkan: run with {
                Counts = [.. run.Counts, new WorldCount(Class: WorkClass.Deterministic, Kind: GpuWork.Dispatches.Name, Node: "world", Pass: "sdf.world$resolve", Source: "gpu", Value: 1L)],
            })
        );

        Assert.Equal(
            actual: Assert.Single(collection: verdict.Failures),
            expected: "vulkan: deterministic kind=gpu.dispatches pass=sdf.world$resolve node=world reads 1 with no ceiling recorded"
        );
    }
    // On another device the per-backend-deterministic magnitudes, however far they move, are not judged, and a line says
    // how many were not and how many required zeros still were; the deterministic counts are judged as on any device.
    [Fact]
    public void APerBackendMagnitudeIsJudgedOnlyOnTheDeviceItWasRecordedOn() {
        var elsewhere = Device(backend: "vulkan", driver: "580.01");
        var verdict = CountersCeilings.Check(
            ceilings: Recorded,
            report: Report(vulkan: Run(backend: "vulkan", device: elsewhere, dispatches: 2L, steps: 9999L))
        );

        Assert.Equal(
            actual: Assert.Single(collection: verdict.Failures),
            expected: "vulkan: deterministic kind=gpu.dispatches.indirect pass=sdf.world$primary node=world is over its ceiling: reads 2, ceiling 1"
        );
        Assert.Equal(
            actual: Assert.Single(collection: verdict.Notes),
            expected: "vulkan: 1 per-backend-deterministic count(s) not judged, 2 required zero(s) still judged: the ceilings were recorded on Example GPU (driver 566.36), this run's device is Example GPU (driver 580.01)"
        );

        // A magnitude that only moves is no failure on the other device, however far.
        Assert.Empty(collection: CountersCeilings.Check(
            ceilings: Recorded,
            report: Report(vulkan: Run(backend: "vulkan", device: elsewhere, steps: 9999L))
        ).Failures);
    }
    // A required zero is a structural contract, so a nonzero reading where one is required fails on a device the ceilings
    // were not recorded on, in the same line as on the recording device.
    [Fact]
    public void ARequiredZeroBrokenOnAnotherDeviceFailsNamingItsKindPassAndNode() {
        var verdict = CountersCeilings.Check(
            ceilings: Recorded,
            report: Report(vulkan: Run(ambientTexels: 12L, backend: "vulkan", cullSteps: 3L, device: Device(backend: "vulkan", driver: "580.01")))
        );

        Assert.Equal(
            actual: verdict.Failures,
            expected: [
                "vulkan: per-backend-deterministic kind=gpu.march.steps pass=sdf.world$cull-args node=world breaks its required zero: reads 3",
                "vulkan: per-backend-deterministic kind=gpu.texels.written pass=sdf.world$ambient node=world breaks its required zero: reads 12",
            ]
        );
    }
    // A zero loosened to a device-following pass's class is one device's policy, not a structural contract: it is not
    // judged elsewhere, while the same pass's kernel-kind zeros are.
    [Fact]
    public void AZeroLoosenedToADeviceFollowingPassIsNotJudgedOnAnotherDevice() {
        var recorded = CountersCeilings.Record(report: Report(vulkan: WithUpload(dispatches: 0L, run: Run(backend: "vulkan"))));
        var elsewhere = Device(backend: "vulkan", driver: "580.01");

        Assert.Empty(collection: CountersCeilings.Check(
            ceilings: recorded,
            report: Report(vulkan: WithUpload(dispatches: 5L, run: Run(backend: "vulkan", device: elsewhere)))
        ).Failures);

        // On the recording device the same reading breaks the zero.
        Assert.Equal(
            actual: Assert.Single(collection: CountersCeilings.Check(
                ceilings: recorded,
                report: Report(vulkan: WithUpload(dispatches: 5L, run: Run(backend: "vulkan")))
            ).Failures),
            expected: "vulkan: per-backend-deterministic kind=gpu.dispatches pass=upload node=world breaks its required zero: reads 5"
        );
    }
    // A pass the recording skipped holds a required zero for each kernel kind everywhere: executing and counting work on
    // another device fails, while the pass still skipped there is judged without a failure.
    [Fact]
    public void ASkippedPassKernelZeroIsJudgedOnAnotherDevice() {
        var skipped = new WorldCountersPass(Class: WorkClass.Deterministic, Label: "sdf.world$shadow", Node: "world", State: GpuPassState.Skipped);
        var recordingRun = Run(backend: "vulkan") with { Passes = [skipped] };
        var recorded = CountersCeilings.Record(report: Report(vulkan: recordingRun));
        var elsewhere = Device(backend: "vulkan", driver: "580.01");

        var verdict = CountersCeilings.Check(
            ceilings: recorded,
            report: Report(vulkan: recordingRun with { Device = elsewhere })
        );

        Assert.Empty(collection: verdict.Failures);
        Assert.Equal(
            actual: Assert.Single(collection: verdict.Notes),
            expected: "vulkan: 1 per-backend-deterministic count(s) not judged, 5 required zero(s) still judged: the ceilings were recorded on Example GPU (driver 566.36), this run's device is Example GPU (driver 580.01)"
        );

        var executing = recordingRun with {
            Counts = [.. recordingRun.Counts, new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.MarchSteps.Name, Node: "world", Pass: "sdf.world$shadow", Source: "gpu", Value: 7L)],
            Device = elsewhere,
            Passes = [skipped with { State = GpuPassState.Executed }],
        };

        Assert.Contains(
            collection: CountersCeilings.Check(ceilings: recorded, report: Report(vulkan: executing)).Failures,
            expected: "vulkan: per-backend-deterministic kind=gpu.march.steps pass=sdf.world$shadow node=world breaks its required zero: reads 7"
        );
    }
    // The recorded files state a required zero exactly where the recorder would: every zero of a kernel kind is marked, so a
    // file recorded or edited without the mark, and a mark on anything else, fails here and not on a foreign device.
    [Fact]
    public void EveryCommittedCeilingsFileMarksExactlyItsKernelKindZerosAsRequired() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var files = Directory.GetFiles(path: Path.Combine(path1: repositoryRoot, path2: "tests", path3: "Puck.Counters"), searchPattern: "*.ceilings.json");

        Assert.NotEmpty(collection: files);

        foreach (var file in files) {
            Assert.True(condition: CountersCeilings.TryRead(ceilings: out var ceilings, path: file, reason: out var reason), userMessage: $"{file}: {reason}");

            foreach (var ceiling in ceilings.Runs.SelectMany(selector: static run => run.Ceilings)) {
                var kind = GpuWork.SubmissionKinds.ToArray().Single(predicate: kind => (kind.Name == ceiling.Kind));

                Assert.True(
                    condition: (ceiling.RequiredZero == CountersCeilings.IsRequiredZero(ceiling: ceiling.Ceiling, kind: kind, recorded: ceiling.Class)),
                    userMessage: $"{Path.GetFileName(path: file)}: kind={ceiling.Kind} pass={(ceiling.Pass ?? "outside")} node={ceiling.Node} ceiling {ceiling.Ceiling} has requiredZero={ceiling.RequiredZero}"
                );
            }
        }
    }
    [Fact]
    public void AnotherWorkloadOrExtentIsRefusedByName() {
        var report = Report(vulkan: Run(backend: "vulkan"));

        Assert.Equal(
            actual: Assert.Single(collection: CountersCeilings.Check(ceilings: Recorded, report: report with { Workload = "tests/Puck.Counters/sky-drift.world.json" }).Failures),
            expected: $"the ceilings hold {CountersCommand.WorldPath} run by {CountersCommand.ScriptPath}, not tests/Puck.Counters/sky-drift.world.json run by {CountersCommand.ScriptPath}"
        );
        Assert.Equal(
            actual: Assert.Single(collection: CountersCeilings.Check(ceilings: Recorded with { Script = "other.script.txt" }, report: report).Failures),
            expected: $"the ceilings hold {CountersCommand.WorldPath} run by other.script.txt, not {CountersCommand.WorldPath} run by {CountersCommand.ScriptPath}"
        );
        Assert.Equal(
            actual: CountersCeilings.Check(ceilings: Recorded, report: Report(vulkan: Run(backend: "vulkan") with { Width = 1280 })).Failures,
            expected: ["vulkan: the ceilings were recorded at 1920x1080, the run is 1280x1080"]
        );
    }
    // A pass the tier skips is in the report only as a state, with no counts: the recording, read through the World's
    // reading and a written and reread report, still holds every submission kind of it at a required zero, of the class
    // the kind reads in the pass, so the pass starting to count fails.
    [Fact]
    public void ASkippedPassIsRecordedAsARequiredZeroForEveryKind() {
        const string Line = """[world.counters: {"sources":[],"gpu":{"device":{"backend":"vulkan","adapter":"Example GPU","vendor":4318,"device":7944,"driver":"566.36","driver.raw":2374860800,"api":"1.4.303","driver.name":"","driver.id":0,"conformance":""},"nodes":[{"name":"world","sample":{"submission":61,"revision":3,"passes":[{"label":"sdf.world$primary","class":"deterministic","state":"executed","counts":{"gpu.dispatches.indirect":1,"gpu.march.steps":4096}},{"label":"sdf.world$ambient","class":"deterministic","state":"skipped"}],"outside":{"gpu.clears":1}},"lifetime":null}]},"allocation":{"gcMode":"workstation, concurrent","windows":{}},"kinds":{"gpu.dispatches.indirect":{"unit":"count","class":"deterministic"},"gpu.march.steps":{"unit":"count","class":"per-backend-deterministic"},"gpu.clears":{"unit":"count","class":"deterministic"}}}]""";

        Assert.True(condition: CountersReading.TryReadLine(
            backend: "vulkan",
            compiler: "toolchain",
            height: 1080,
            line: Line,
            reason: out var reason,
            run: out var run,
            width: 1920
        ), userMessage: reason);

        var directory = Directory.CreateTempSubdirectory(prefix: "puck-counters-ceilings-law-");

        try {
            var path = Path.Combine(path1: directory.FullName, path2: "report.json");

            CountersCommand.WriteReport(path: path, report: Report(directx: (run with { Backend = "directx", Device = Device(backend: "directx") }), vulkan: run));
            Assert.True(condition: CountersCommand.TryReadReport(path: path, reason: out var readReason, report: out var report), userMessage: readReason);

            var ceilings = CountersCeilings.Record(report: report);
            var ambient = ceilings.Runs[0].Ceilings.Where(predicate: static ceiling => (ceiling.Pass == "sdf.world$ambient")).ToArray();

            Assert.Equal(
                actual: ambient.Select(selector: static ceiling => ceiling.Kind),
                expected: GpuWork.SubmissionKinds.ToArray().Select(selector: static kind => kind.Name)
            );
            Assert.All(collection: ambient, action: static ceiling => Assert.Equal(actual: ceiling.Ceiling, expected: 0L));
            Assert.Equal(
                actual: ambient.Single(predicate: static ceiling => (ceiling.Kind == GpuWork.TexelsWritten.Name)).Class,
                expected: WorkClass.PerBackendDeterministic
            );
            Assert.Equal(
                actual: ambient.Single(predicate: static ceiling => (ceiling.Kind == GpuWork.Dispatches.Name)).Class,
                expected: WorkClass.Deterministic
            );
            Assert.Empty(collection: CountersCeilings.Check(ceilings: ceilings, report: report).Failures);

            // The pass executing and writing texels breaks its recorded zero.
            var executing = report.Runs[0] with {
                Counts = [.. report.Runs[0].Counts, new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.TexelsWritten.Name, Node: "world", Pass: "sdf.world$ambient", Source: "gpu", Value: 5L)],
                Passes = [.. report.Runs[0].Passes.Select(selector: static pass => (pass with { State = GpuPassState.Executed }))],
            };

            Assert.Contains(
                collection: CountersCeilings.Check(ceilings: ceilings, report: report with { Runs = [executing, report.Runs[1]] }).Failures,
                expected: "vulkan: per-backend-deterministic kind=gpu.texels.written pass=sdf.world$ambient node=world breaks its required zero: reads 5"
            );
        } finally {
            directory.Delete(recursive: true);
        }
    }
    // Every recorded expectation must be measured: a node the run no longer reports fails each of its ceilings.
    [Fact]
    public void ARecordedNodeTheRunDoesNotMeasureFails() {
        var run = Run(backend: "vulkan");
        var verdict = CountersCeilings.Check(
            ceilings: Recorded,
            report: Report(vulkan: run with {
                Counts = [.. run.Counts.Where(predicate: static count => (count.Node != "world"))],
            })
        );

        Assert.Equal(
            actual: verdict.Failures,
            expected: [
                "vulkan: deterministic kind=gpu.dispatches.indirect pass=sdf.world$primary node=world was recorded but not measured",
                "vulkan: per-backend-deterministic kind=gpu.march.steps pass=sdf.world$primary node=world was recorded but not measured",
                "vulkan: per-backend-deterministic kind=gpu.march.steps pass=sdf.world$cull-args node=world was recorded but not measured",
                "vulkan: per-backend-deterministic kind=gpu.texels.written pass=sdf.world$ambient node=world was recorded but not measured",
                "vulkan: deterministic kind=gpu.clears pass=outside node=world was recorded but not measured",
            ]
        );
    }
    // A count whose class moved is no longer the count its ceiling holds: raised and reclassified as pacing, which no
    // ceiling would hold, it fails by its class rather than escaping the check.
    [Fact]
    public void ARaisedCountReclassifiedAsPacingFails() {
        var run = Run(backend: "vulkan");
        var verdict = CountersCeilings.Check(
            ceilings: Recorded,
            report: Report(vulkan: run with {
                Counts = [.. run.Counts.Select(selector: static count => (((count.Kind == GpuWork.MarchSteps.Name) && (count.Pass == "sdf.world$primary"))
                    ? (count with { Class = WorkClass.Pacing, Value = 9999L })
                    : count))],
            })
        );

        Assert.Equal(
            actual: Assert.Single(collection: verdict.Failures),
            expected: "vulkan: per-backend-deterministic kind=gpu.march.steps pass=sdf.world$primary node=world was measured as pacing"
        );
    }
    [Fact]
    public void ADeviceFollowingKindsZeroCannotBeReadAsARequiredZero() {
        var recorded = CountersCeilings.Record(report: Report(vulkan: WithUpload(dispatches: 0L, run: Run(backend: "vulkan"))));
        var run = recorded.Runs[0];
        var upload = Assert.Single(collection: run.Ceilings, predicate: static ceiling => (ceiling.Pass == "upload"));
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-counters-ceilings-law-");

        try {
            var path = Path.Combine(path1: directory.FullName, path2: "counters.ceilings.json");

            CountersCeilings.Write(ceilings: recorded with { Runs = [run with { Ceilings = [upload with { RequiredZero = true }] }] }, path: path);

            Assert.False(condition: CountersCeilings.TryRead(ceilings: out _, path: path, reason: out var reason));
            Assert.Contains(actualString: reason, expectedSubstring: "kind=gpu.dispatches pass=upload node=world requiredZero does not match its kind, class and ceiling");
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AKernelZeroCannotBeReadWithoutItsRequiredZeroMark(bool explicitFalse) {
        var run = Recorded.Runs[0];
        var zero = run.Ceilings.First(predicate: static ceiling => ceiling.RequiredZero);
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-counters-ceilings-law-");

        try {
            var path = Path.Combine(path1: directory.FullName, path2: "counters.ceilings.json");

            CountersCeilings.Write(ceilings: Recorded with { Runs = [run with { Ceilings = [zero with { RequiredZero = false }] }] }, path: path);

            if (explicitFalse) {
                File.WriteAllText(
                    contents: File.ReadAllText(path: path).Replace(comparisonType: StringComparison.Ordinal, newValue: "\"ceiling\": 0, \"requiredZero\": false", oldValue: "\"ceiling\": 0"),
                    path: path
                );
            }

            Assert.False(condition: CountersCeilings.TryRead(ceilings: out _, path: path, reason: out var reason));
            Assert.Contains(actualString: reason, expectedSubstring: "kind=gpu.march.steps pass=sdf.world$cull-args node=world requiredZero does not match its kind, class and ceiling");
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void WrittenCeilingsReadBackAsWrittenAndAForeignFileIsRefused() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-counters-ceilings-law-");

        try {
            var path = Path.Combine(path1: directory.FullName, path2: "counters.ceilings.json");

            CountersCeilings.Write(ceilings: Recorded, path: path);

            Assert.True(condition: CountersCeilings.TryRead(ceilings: out var read, path: path, reason: out var reason), userMessage: reason);
            Assert.Equal(
                actual: JsonSerializer.Serialize(value: read, jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings),
                expected: JsonSerializer.Serialize(value: Recorded, jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings)
            );

            // A required zero is written as such, and a ceiling that is not a zero of a per-backend class cannot be one.
            Assert.Contains(actualString: File.ReadAllText(path: path), expectedSubstring: "\"requiredZero\": true");
            Assert.DoesNotContain(actualString: File.ReadAllText(path: path), expectedSubstring: "\"requiredZero\": false");

            var vulkan = Recorded.Runs[0];
            var raised = Recorded with {
                Runs = [vulkan with { Ceilings = [vulkan.Ceilings.First(predicate: static ceiling => ceiling.RequiredZero) with { Ceiling = 1L }] }],
            };

            CountersCeilings.Write(ceilings: raised, path: path);
            Assert.False(condition: CountersCeilings.TryRead(ceilings: out _, path: path, reason: out var malformed));
            Assert.Contains(actualString: malformed, expectedSubstring: "requiredZero does not match its kind, class and ceiling");

            File.WriteAllText(contents: "{\"schema\":\"puck.counters.report.v1\",\"workload\":\"w\",\"script\":\"s\",\"runs\":[]}", path: path);
            Assert.False(condition: CountersCeilings.TryRead(ceilings: out _, path: path, reason: out var foreign));
            Assert.Contains(actualString: foreign, expectedSubstring: "a foreign document");
        } finally {
            directory.Delete(recursive: true);
        }
    }
}
