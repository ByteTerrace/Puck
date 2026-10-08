using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Cli.Counters;
using Puck.Cli.Gate;
using Puck.Testing;
using Puck.World;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: a workload <c>puck counters --record</c> records for the first time becomes exactly one
/// <c>puck gate</c> counters step, and a second device's record keeps that step (<c>CountersCeilingsLawTests</c> holds
/// the recording itself).</summary>
public sealed class CountersGateStepLawTests {
    private static GpuDeviceIdentity Device(string backend, uint deviceId = 0x1F08U) => new(
        AdapterName: "Example GPU",
        ApiVersion: "1.4.303",
        Backend: backend,
        DeviceId: deviceId,
        DriverVersion: "566.36",
        DriverVersionRaw: 0x8D8D8000UL,
        VendorId: 0x10DEU
    );

    private static GpuDeviceIdentity Elsewhere { get; } = Device(backend: "vulkan", deviceId: 0x2786U);

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

    [InlineData("nexus")]
    [InlineData("courtyard")]
    [Theory]
    public void RecordingAnUnrecordedDenseWorkloadCreatesDeviceCeilingsAndOneGateStep(string workload) {
        using var directory = new TemporaryDirectory(prefix: "puck-counters-dense-ledger-law-");
        var stem = $"tests/Puck.Counters/{workload}";
        var relativeCeilings = (stem + ".ceilings.json");
        var path = Path.Combine(path1: directory.RootPath, path2: relativeCeilings);
        var report = Report(vulkan: Run(backend: "vulkan")) with { Workload = (stem + ".world.json") };

        report = report with {
            Runs = [.. report.Runs.Select(selector: static run => run with {
                Counts = [.. run.Counts,
                    new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.ShapesEvaluated.Name, Node: "world", Pass: "sdf.world$primary", Source: "gpu", Value: 123L),
                    new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.ShapeGradients.Name, Node: "world", Pass: "sdf.world$primary", Source: "gpu", Value: 12L),
                    new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.ShapesEvaluated.Name, Node: "world", Pass: "sdf.world$tape", Source: "gpu", Value: 17L),
                ],
            })],
        };
        directory.WriteText(name: report.Workload, text: "{}");
        directory.WriteText(name: report.Script, text: "world.counters --json");
        var step = GatePlan.Steps.Single(predicate: static candidate => (candidate.Kind == GateStepKind.Counters));

        Assert.False(condition: File.Exists(path: path));
        Assert.Empty(collection: GatePlan.CounterWorkloads(repositoryRoot: directory.RootPath, step: step));
        Assert.True(condition: CountersCommand.TryRecord(path: path, reason: out var first, report: report), userMessage: first);
        Assert.True(condition: CountersCeilings.TryRead(ceilings: out var ceilings, path: path, reason: out var reason), userMessage: reason);
        Assert.Equal(actual: (ceilings.Workload, ceilings.Script), expected: (report.Workload, report.Script));
        Assert.Equal(actual: ceilings.Backends.Select(selector: static backend => backend.Backend), expected: ["vulkan", "directx"]);
        Assert.All(collection: ceilings.Backends, action: static backend => {
            Assert.Contains(collection: backend.Ceilings, filter: static ceiling => ((ceiling.Kind == GpuWork.IndirectDispatches.Name) && (ceiling.Ceiling == 1L)));
            Assert.Contains(collection: backend.Ceilings, filter: static ceiling => ((ceiling.Kind == GpuWork.MarchSteps.Name) && (ceiling.Pass == "sdf.world$cull-args") && (ceiling.Ceiling == 0L)));
            var device = Assert.Single(collection: backend.Devices);

            Assert.Equal(actual: device.Device, expected: Device(backend: backend.Backend));
            Assert.Equal(actual: device.Ceilings.Select(selector: static ceiling => (ceiling.Pass, ceiling.Kind, ceiling.Ceiling)), expected: [
                ("sdf.world$primary", GpuWork.MarchSteps.Name, 4096L),
                ("sdf.world$primary", GpuWork.ShapesEvaluated.Name, 123L),
                ("sdf.world$primary", GpuWork.ShapeGradients.Name, 12L),
                ("sdf.world$tape", GpuWork.ShapesEvaluated.Name, 17L),
            ]);
        });
        Assert.Empty(collection: CountersCeilings.Check(ceilings: ceilings, report: report).Failures);
        var discovered = Assert.Single(collection: GatePlan.CounterWorkloads(repositoryRoot: directory.RootPath, step: step));

        Assert.Equal(actual: discovered.Name, expected: ("counters " + workload));
        Assert.Equal(actual: discovered.Arguments, expected: ["counters", "--check", "--world", report.Workload, "--ceilings", relativeCeilings, "--script", report.Script]);
        var elsewhere = report with {
            Runs = [.. report.Runs.Select(selector: static run => run with { Device = Device(backend: run.Backend, deviceId: 0x2786U) })],
        };

        Assert.True(condition: CountersCommand.TryRecord(path: path, reason: out var second, report: elsewhere), userMessage: second);
        Assert.True(condition: CountersCeilings.TryRead(ceilings: out var merged, path: path, reason: out var mergedReason), userMessage: mergedReason);
        Assert.All(collection: merged.Backends, action: static backend => Assert.Equal(
            actual: backend.Devices.Select(selector: static device => device.Device.DeviceId), expected: [0x1F08U, 0x2786U]));
        Assert.Empty(collection: CountersCeilings.Check(ceilings: merged, report: report).Failures);
        Assert.Empty(collection: CountersCeilings.Check(ceilings: merged, report: elsewhere).Failures);
        Assert.Equal(actual: Assert.Single(collection: GatePlan.CounterWorkloads(repositoryRoot: directory.RootPath, step: step)).Arguments, expected: discovered.Arguments);
    }
}
