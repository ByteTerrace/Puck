using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Cli.Counters;
using Puck.Testing;
using Puck.World;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a <c>puck.counters.ceilings.v1</c> ledger holds one record per device and backend, keyed by the
/// adapter's PCI vendor and device and the driver implementation, beside the ceilings every device shares. <c>puck counters
/// --check</c> judges every count of a run against its own device's record and the shared ceilings: a device with no record
/// fails by name, never as a silent skip, and a count its record lacks fails on that device alone. A driver update keeps the
/// record and is named in a note. <c>puck counters --record</c> adds or replaces only the current device's record and the
/// shared ceilings, leaving every other device's record byte for byte as it was, and refuses a record whose shared ceiling
/// another device holds as its own reading. <c>--report</c> judges a saved report without booting anything.
/// </summary>
public sealed class CountersDeviceCeilingsLawTests {
    private const string Floor = "Floor GPU";
    private const string Lead = "Lead GPU";

    private static GpuDeviceIdentity Device(string backend, string adapter, uint deviceId, ulong driver = 0x8D8D8000UL) => new(
        AdapterName: adapter,
        ApiVersion: "1.4.303",
        Backend: backend,
        DeviceId: deviceId,
        DriverVersion: ((driver == 0x8D8D8000UL) ? "566.36" : "580.01"),
        DriverVersionRaw: driver,
        VendorId: 0x10DEU
    );
    private static GpuDeviceIdentity FloorDevice(string backend) => Device(adapter: Floor, backend: backend, deviceId: 0x1F08U);
    private static GpuDeviceIdentity LeadDevice(string backend) => Device(adapter: Lead, backend: backend, deviceId: 0x2786U);
    // One backend's run: a deterministic dispatch, the primary march's steps (a device's own magnitude), a required zero, and
    // optionally the texels the primary pass writes, a kind a later code version counts.
    private static WorldCountersRun Run(string backend, GpuDeviceIdentity device, long steps, long? texels = null) => new(
        Backend: backend,
        Compiler: "toolchain",
        Counts: [
            new WorldCount(Class: WorkClass.Deterministic, Kind: GpuWork.IndirectDispatches.Name, Node: "world", Pass: "sdf.world$primary", Source: "gpu", Value: 1L),
            new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.MarchSteps.Name, Node: "world", Pass: "sdf.world$primary", Source: "gpu", Value: steps),
            new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.MarchSteps.Name, Node: "world", Pass: "sdf.world$cull-args", Source: "gpu", Value: 0L),
            .. ((texels is { } written)
                ? new[] { new WorldCount(Class: WorkClass.PerBackendDeterministic, Kind: GpuWork.TexelsWritten.Name, Node: "world", Pass: "sdf.world$primary", Source: "gpu", Value: written) }
                : []),
        ],
        Device: device,
        GcMode: "workstation, concurrent",
        Height: 1080,
        Passes: [],
        Width: 1920
    );
    private static WorldCountersReport Report(Func<string, GpuDeviceIdentity> device, long steps, long? texels = null) => new(
        Revision: new WorldCountersRevision(Commit: "0123abcd", SourceState: "state-key"),
        Runs: [
            Run(backend: "vulkan", device: device(arg: "vulkan"), steps: steps, texels: texels),
            Run(backend: "directx", device: device(arg: "directx"), steps: (steps + 1L), texels: texels),
        ],
        Script: CountersCommand.ScriptPath,
        Workload: CountersCommand.WorldPath
    );
    private static string RecordIn(string path, string backend, string adapter) {
        using var document = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: path));

        foreach (var entry in document.RootElement.GetProperty(propertyName: "backends").EnumerateArray()) {
            if (entry.GetProperty(propertyName: "backend").GetString() != backend) {
                continue;
            }

            foreach (var device in entry.GetProperty(propertyName: "devices").EnumerateArray()) {
                if (device.GetProperty(propertyName: "device").GetProperty(propertyName: "adapter").GetString() == adapter) {
                    return device.GetRawText();
                }
            }
        }

        throw new InvalidOperationException(message: $"{path} holds no {backend} record for {adapter}");
    }

    // A run on a device with no record fails naming the device and the verb that records it, never passing silently; the
    // shared ceilings are still judged there, so a deterministic count over its ceiling fails beside it.
    [Fact]
    public void ADeviceWithNoRecordFailsByName() {
        var ceilings = CountersCeilings.Record(report: Report(device: FloorDevice, steps: 4096L));
        var verdict = CountersCeilings.Check(ceilings: ceilings, report: Report(device: LeadDevice, steps: 9999L));

        Assert.Equal(
            actual: verdict.Failures,
            expected: [
                "vulkan: no ceilings recorded for Lead GPU (vendor=0x10de device=0x2786); run puck counters --record on it",
                "directx: no ceilings recorded for Lead GPU (vendor=0x10de device=0x2786); run puck counters --record on it",
            ]
        );
        Assert.Empty(collection: verdict.Notes);
        Assert.Empty(collection: CountersCeilings.Check(ceilings: ceilings, report: Report(device: FloorDevice, steps: 4096L)).Failures);
    }
    // Recording a second device adds its record after the first and leaves the first byte for byte as it was; re-recording
    // the first replaces it in place and leaves the second as it was. Each device is then judged against its own record.
    [Fact]
    public void RecordingOneDeviceLeavesAnotherDevicesRecordByteIdentical() {
        using var directory = new TemporaryDirectory(prefix: "puck-counters-devices-law-");

        var path = Path.Combine(path1: directory.RootPath, path2: "counters.ceilings.json");

        Assert.True(condition: CountersCommand.TryRecord(path: path, reason: out var first, report: Report(device: FloorDevice, steps: 4096L)), userMessage: first);

        var floorVulkan = RecordIn(adapter: Floor, backend: "vulkan", path: path);
        var floorDirectX = RecordIn(adapter: Floor, backend: "directx", path: path);

        Assert.True(condition: CountersCommand.TryRecord(path: path, reason: out var second, report: Report(device: LeadDevice, steps: 3000L)), userMessage: second);
        Assert.Equal(actual: RecordIn(adapter: Floor, backend: "vulkan", path: path), expected: floorVulkan);
        Assert.Equal(actual: RecordIn(adapter: Floor, backend: "directx", path: path), expected: floorDirectX);

        var leadVulkan = RecordIn(adapter: Lead, backend: "vulkan", path: path);

        Assert.True(condition: CountersCommand.TryRecord(path: path, reason: out var third, report: Report(device: FloorDevice, steps: 4000L)), userMessage: third);
        Assert.Equal(actual: RecordIn(adapter: Lead, backend: "vulkan", path: path), expected: leadVulkan);
        Assert.NotEqual(actual: RecordIn(adapter: Floor, backend: "vulkan", path: path), expected: floorVulkan);
        Assert.True(condition: CountersCeilings.TryRead(ceilings: out var ceilings, path: path, reason: out var reason), userMessage: reason);
        Assert.All(collection: ceilings.Backends, action: static backend => Assert.Equal(
            actual: backend.Devices.Select(selector: static device => device.Device.AdapterName),
            expected: [Floor, Lead]
        ));

        // The lead device's 3000 steps hold on it and the floor's 4000 on the floor; each is over the other's ceiling.
        Assert.Empty(collection: CountersCeilings.Check(ceilings: ceilings, report: Report(device: LeadDevice, steps: 3000L)).Failures);
        Assert.Empty(collection: CountersCeilings.Check(ceilings: ceilings, report: Report(device: FloorDevice, steps: 4000L)).Failures);
        Assert.Contains(
            collection: CountersCeilings.Check(ceilings: ceilings, report: Report(device: LeadDevice, steps: 4000L)).Failures,
            expected: "vulkan: per-backend-deterministic kind=gpu.march.steps pass=sdf.world$primary detail=- node=world is over its ceiling: reads 4000, ceiling 3000"
        );
    }
    // A kind a device's record lacks fails on that device by name, while the device whose record holds it passes.
    [Fact]
    public void AKindMissingFromADevicesRecordFailsOnThatDevice() {
        Assert.True(condition: CountersCeilings.TryMerge(
            existing: CountersCeilings.Record(report: Report(device: FloorDevice, steps: 4096L)),
            merged: out var ceilings,
            reason: out var reason,
            recorded: CountersCeilings.Record(report: Report(device: LeadDevice, steps: 3000L, texels: 2048L))
        ), userMessage: reason);

        Assert.Empty(collection: CountersCeilings.Check(ceilings: ceilings, report: Report(device: LeadDevice, steps: 3000L, texels: 2048L)).Failures);
        Assert.Equal(
            actual: CountersCeilings.Check(ceilings: ceilings, report: Report(device: FloorDevice, steps: 4096L, texels: 2048L)).Failures,
            expected: [
                "vulkan: per-backend-deterministic kind=gpu.texels.written pass=sdf.world$primary detail=- node=world reads 2048 with no ceiling recorded",
                "directx: per-backend-deterministic kind=gpu.texels.written pass=sdf.world$primary detail=- node=world reads 2048 with no ceiling recorded",
            ]
        );
    }
    // A driver update is the same device: its record still judges the run, and a note names both drivers.
    [Fact]
    public void ADriverUpdateKeepsTheDevicesRecordAndNamesBothDrivers() {
        var ceilings = CountersCeilings.Record(report: Report(device: FloorDevice, steps: 4096L));
        var updated = CountersCeilings.Check(ceilings: ceilings, report: Report(device: static backend => Device(adapter: Floor, backend: backend, deviceId: 0x1F08U, driver: 0x91000000UL), steps: 4097L));

        Assert.Contains(
            collection: updated.Failures,
            expected: "vulkan: per-backend-deterministic kind=gpu.march.steps pass=sdf.world$primary detail=- node=world is over its ceiling: reads 4097, ceiling 4096"
        );
        Assert.Equal(
            actual: updated.Notes[0],
            expected: "vulkan: the ceilings for Floor GPU (vendor=0x10de device=0x1f08) were recorded under driver 566.36, this run's driver is 580.01"
        );
    }
    // A record whose shared ceiling another device holds as its own reading would leave a ledger that judges one count twice,
    // so it refuses naming the count and the device; a record of another workload refuses too, and either leaves the file.
    [Fact]
    public void ARecordThatWouldMixSharedAndDeviceCeilingsRefuses() {
        var floor = CountersCeilings.Record(report: Report(device: FloorDevice, steps: 4096L, texels: 2048L));

        Assert.False(condition: CountersCeilings.TryMerge(
            existing: floor,
            merged: out _,
            reason: out var mixed,
            recorded: CountersCeilings.Record(report: Report(device: LeadDevice, steps: 3000L, texels: 0L))
        ));
        Assert.Equal(
            actual: mixed,
            expected: "vulkan: per-backend-deterministic kind=gpu.texels.written pass=sdf.world$primary detail=- node=world is a ceiling every device shares in this record and Floor GPU (vendor=0x10de device=0x1f08)'s own reading in the ledger; record that device again on it"
        );
        Assert.False(condition: CountersCeilings.TryMerge(
            existing: floor,
            merged: out _,
            reason: out var workload,
            recorded: CountersCeilings.Record(report: Report(device: LeadDevice, steps: 3000L) with { Workload = "tests/Puck.Counters/sky-still.world.json" })
        ));
        Assert.StartsWith(actualString: workload, expectedStartString: $"the ceilings hold {CountersCommand.WorldPath} run by {CountersCommand.ScriptPath}, not tests/Puck.Counters/sky-still.world.json");

        using var directory = new TemporaryDirectory(prefix: "puck-counters-devices-law-");

        var path = Path.Combine(path1: directory.RootPath, path2: "counters.ceilings.json");

        CountersCeilings.Write(ceilings: floor, path: path);

        var before = File.ReadAllBytes(path: path);

        Assert.False(condition: CountersCommand.TryRecord(path: path, reason: out var refused, report: Report(device: LeadDevice, steps: 3000L, texels: 0L)));
        Assert.EndsWith(actualString: refused, expectedEndString: "record that device again on it, so no ceilings are recorded");
        Assert.Equal(actual: File.ReadAllBytes(path: path), expected: before);
    }
    // A ledger recording one device twice, a shared ceiling that is a device's reading, or one count both shared and a
    // device's own is no ceilings document.
    [Fact]
    public void AMalformedDeviceLedgerIsRefusedByName() {
        var ceilings = CountersCeilings.Record(report: Report(device: FloorDevice, steps: 4096L));
        var vulkan = ceilings.Backends[0];
        var steps = Assert.Single(collection: vulkan.Devices[0].Ceilings);
        var dispatches = vulkan.Ceilings.First(predicate: static ceiling => (ceiling.Class == WorkClass.Deterministic));
        using var directory = new TemporaryDirectory(prefix: "puck-counters-devices-law-");

        var path = Path.Combine(path1: directory.RootPath, path2: "counters.ceilings.json");

        string Refusal(WorldCountersBackendCeilings backend) {
            CountersCeilings.Write(ceilings: ceilings with { Backends = [backend] }, path: path);
            Assert.False(condition: CountersCeilings.TryRead(ceilings: out _, path: path, reason: out var reason));

            return reason;
        }

        Assert.Equal(
            actual: Refusal(backend: vulkan with { Devices = [vulkan.Devices[0], vulkan.Devices[0]] }),
            expected: "a malformed device record: vulkan records Floor GPU (vendor=0x10de device=0x1f08) twice"
        );
        Assert.Equal(
            actual: Refusal(backend: vulkan with { Ceilings = [.. vulkan.Ceilings, steps] }),
            expected: "a malformed ceiling: vulkan per-backend-deterministic kind=gpu.march.steps pass=sdf.world$primary detail=- node=world is one device's reading among the ceilings every device shares"
        );
        Assert.Equal(
            actual: Refusal(backend: vulkan with { Devices = [vulkan.Devices[0] with { Ceilings = [steps, dispatches] }] }),
            expected: "a malformed ceiling: vulkan deterministic kind=gpu.dispatches.indirect pass=sdf.world$primary detail=- node=world is a ceiling every device shares, recorded as Floor GPU (vendor=0x10de device=0x1f08)'s own"
        );
        Assert.Equal(
            actual: Refusal(backend: vulkan with { Devices = [vulkan.Devices[0] with { Device = FloorDevice(backend: "directx") }] }),
            expected: "a malformed device record: Floor GPU (vendor=0x10de device=0x1f08) ran on directx, recorded under vulkan"
        );
    }
    // A saved report is judged without booting the workload: on a device with no record the verb fails by name and exits 1,
    // and on the recorded device the same report holds.
    [Fact]
    public void ASavedReportIsJudgedWithoutBootingTheWorkload() {
        using var directory = new TemporaryDirectory(prefix: "puck-counters-devices-law-");

        var ceilingsPath = Path.Combine(path1: directory.RootPath, path2: "counters.ceilings.json");
        var leadPath = Path.Combine(path1: directory.RootPath, path2: "lead.report.json");
        var floorPath = Path.Combine(path1: directory.RootPath, path2: "floor.report.json");

        CountersCeilings.Write(ceilings: CountersCeilings.Record(report: Report(device: FloorDevice, steps: 4096L)), path: ceilingsPath);
        CountersCommand.WriteReport(path: leadPath, report: Report(device: LeadDevice, steps: 4096L));
        CountersCommand.WriteReport(path: floorPath, report: Report(device: FloorDevice, steps: 4096L));

        var (leadExit, leadOutput, _) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["counters", "--check", "--ceilings", ceilingsPath, "--report", leadPath]));

        Assert.Equal(actual: leadExit, expected: CliExit.Failed);
        Assert.Contains(actualString: leadOutput, expectedSubstring: "counters: vulkan: no ceilings recorded for Lead GPU (vendor=0x10de device=0x2786); run puck counters --record on it");

        var (floorExit, floorOutput, _) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["counters", "--check", "--ceilings", ceilingsPath, "--report", floorPath]));

        Assert.Equal(actual: floorExit, expected: CliExit.Success);
        Assert.Contains(actualString: floorOutput, expectedSubstring: " hold");

        // A saved report names its own workload, so the options that select a run refuse beside it.
        var (refusedExit, _, refusedError) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["counters", "--check", "--ceilings", ceilingsPath, "--report", floorPath, "--world", leadPath]));

        Assert.Equal(actual: refusedExit, expected: CliExit.Refused);
        Assert.Contains(actualString: refusedError, expectedSubstring: "--world, --script and --output select a run");
    }
}
