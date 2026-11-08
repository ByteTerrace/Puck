using System.Security.Cryptography;
using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Addons;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A durable boundary preserves guest memory and admits only external edits after a crash.
/// The guest's increasing loop cost witnesses its private global without exporting or checkpointing that global.</summary>
public sealed class AddonCheckpointLawTests {
    private const string Counter = """
        (module
          (memory (export "memory") 1)
          (data (i32.const 0) "\02\00\01\00\00\00\00\00\00\00\00\00\00\00\00\00\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
          (global $ticks (mut i32) (i32.const 0))
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_channels_ptr") (result i32) (i32.const 0))
          (func (export "puck_channels_count") (result i32) (i32.const 2))
          (func (export "puck_out_ptr") (result i32) (i32.const 64))
          (func (export "puck_out_cap") (result i32) (i32.const 0))
          (func (export "puck_in_ptr") (result i32) (i32.const 128))
          (func (export "puck_in_cap") (result i32) (i32.const 1))
          (func (export "puck_on_tick") (param i32) (result i32)
            (local $remaining i32)
            (global.set $ticks (i32.add (global.get $ticks) (i32.const 1)))
            (local.set $remaining (global.get $ticks))
            (loop $work
              (local.set $remaining (i32.sub (local.get $remaining) (i32.const 1)))
              (br_if $work (local.get $remaining)))
            (i32.const 0)))
        """;

    private static WorldDefinition Document(TemporaryDirectory directory, string? module = null, ulong fuel = 1_000_000) {
        var bytes = Encoding.UTF8.GetBytes(s: (module ?? Counter));
        var path = directory.PathOf(name: "counter.wat").Replace(newChar: '/', oldChar: '\\');

        File.WriteAllBytes(bytes: bytes, path: path);
        return Fixtures.BuildDocument() with {
            ChannelsRaw = [.. Fixtures.BuildDocument().Channels, new("held", ChannelShape.Binary, Composition: true)],
            AddonsRaw = [new WorldAddonRow("counter", path,
                ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))), fuel, true)],
        };
    }
    private static WorldReplayTape Tape(WorldFixture fixture, TemporaryDirectory directory) => new(
        addonHostFactory: static (definition, server) => WorldAddonRuntime.Create(definition: definition, server: server),
        engines: [],
        liveServer: fixture.Server,
        machineHostFactory: Fixtures.MachineHostFactory,
        profiles: fixture.Server.Profiles,
        stateRoot: new WorldStateRoot(path: directory.RootPath),
        transport: new LoopbackTransport(server: fixture.Server)
    );

    [Fact]
    public void AnEpochInterruptedRowDoesNotStallAnotherRowOrPublishAPartialCheckpoint() {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-deadline-");
        var module = (Counter[..Counter.IndexOf(comparisonType: StringComparison.Ordinal, value: "(func (export \"puck_on_tick\")")] + """
            (func (export "puck_on_tick") (param i32) (result i32)
              (loop $work
                (memory.fill (i32.const 0) (i32.const 0) (i32.const 16777216))
                (br $work))
              (i32.const 0)))
            """);

        module = module.Replace(comparisonType: StringComparison.Ordinal, newValue: "(memory (export \"memory\") 256)", oldValue: "(memory (export \"memory\") 1)");
        using var slow = HostRow.Build("a-slow", Document(directory: directory, fuel: WorldAddonLimits.FuelPerAddon, module: module));
        using var fast = HostRow.Build("z-fast");
        // The module normally hits Wasmtime 48's byte-charged fuel first. Lift fuel only at the runtime
        // boundary to isolate the independent wall-time guard from document admission's fuel ceiling.
        var execution = slow.Server.Definition with {
            AddonsRaw = slow.Server.Definition.Addons.Select(selector: row => row with { Fuel = long.MaxValue }).ToArray(),
        };
        using var runtime = WorldAddonRuntime.Create(definition: execution, server: slow.Server);
        using var host = new WorldInstanceHost(WorldEmbodiedSeats.None, new WorldSessionResolver(), Guid.NewGuid(),
            new WorldStateRoot(path: directory.RootPath), CancellationToken.None, Fixtures.MachineHostFactory);

        foreach (var row in new[] { slow, fast }) {
            host.Admit(row: row.Instance);
            host.RestoreRow(row: row.Instance, slice: host.CaptureRow(row: row.Instance) with { Retained = true });
        }
        host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        Assert.True(condition: slow.Server.IsRetiring);
        Assert.Equal(0UL, slow.Server.CompletedTick);
        Assert.Equal(1UL, fast.Server.CompletedTick);
        Assert.False(condition: slow.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out _, out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "incomplete tick");
        host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        Assert.Equal(2UL, fast.Server.CompletedTick);
    }
    [Fact]
    public void AnUncapturableGuestIsRefusedBeforeMounting() {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-table-");
        var module = Counter.Replace(comparisonType: StringComparison.Ordinal, newValue: "(module (table 1 funcref)", oldValue: "(module");
        using var fixture = Fixtures.FreshServer(Document(directory, module));

        Assert.False(condition: WorldAddonRuntime.TryCreate(fixture.Server.Definition, fixture.Server, out var runtime, out var reason));
        Assert.Null(@object: runtime);
        Assert.Null(@object: fixture.Server.Addons);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "durable guest state unavailable");
    }
    [InlineData(0)]
    [InlineData(5)]
    [Theory]
    public void RecordingPreservesGuestMemoryAndReDrivesItsCheckpoint(int warmTicks) {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-arm-");
        using var fixture = Fixtures.FreshServer(Document(directory));

        fixture.JoinSeat();
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);
        var tape = Tape(directory: directory, fixture: fixture);

        for (var tick = 0; (tick < warmTicks); tick++) { fixture.Step(); }
        var before = Assert.Single(collection: runtime.DescribeCost());

        Assert.True(condition: tape.TryBeginRecording(name: "late-addon", refusal: out var refusal), userMessage: refusal);
        Assert.Equal(before, Assert.Single(collection: runtime.DescribeCost()));
        fixture.Step(); tape.NoteTick();
        Assert.True(condition: (Assert.Single(collection: runtime.DescribeCost()).LastTickFuelConsumed > before.LastTickFuelConsumed));
        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); tape.NoteTick(); }
        var stop = tape.StopRecording();

        Assert.Null(@object: stop.VerifyFault);
        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());
        Assert.True(condition: tape.TryBeginDrive(documentPath: null, forkName: null, name: "late-addon", refusal: out refusal, toTick: 1), userMessage: refusal);
        Assert.Equal(before.TotalFuelConsumed, Assert.Single(collection: runtime.DescribeCost()).TotalFuelConsumed);
        tape.InjectDriveTick(); fixture.Step(); tape.NoteTick();
        Assert.Equal(WorldReplayMode.Idle, tape.Mode);
    }
    [Fact]
    public void CrashRecoveryRewindsGuestOutputWithItsMemoryAndRetainsExternalEdits() {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-crash-");
        using var fixture = Fixtures.FreshServer(Document(directory));
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);

        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); }
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        var atCheckpoint = Assert.Single(collection: runtime.DescribeCost());
        var tail = new List<(ulong Tick, ulong EngineTick, WorldMutation Mutation)>();

        fixture.Server.MutationJournalTap = (tick, engineTick, mutation) => tail.Add(item: (tick, engineTick, mutation));
        // These are the exact enqueue and drain seams used by a guest, with an expired completion token.
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(
            Row: new WorldStateRow(Name: CellName.Parse(candidate: "guest-effect"), Kind: CellKind.Int,
                Cells: [new StateCell(CellName.Parse(candidate: "fall"), CellValue.Int(value: 0))]), Principal: Principal.Console), sourceAddonInstanceId: 17);
        fixture.Step();
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "guest-effect", "fall", 1, WorldDocumentWriteKind.Set), sourceAddonInstanceId: 17);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(
            Row: new WorldStateRow(Name: CellName.Parse(candidate: "external-edit"), Kind: CellKind.Int), Principal: Principal.Console));
        fixture.Step();
        Assert.Contains(collection: fixture.Server.Definition.State, filter: row => (row.Name.Value == "guest-effect"));
        Assert.Single(collection: tail);
        fixture.Server.MutationJournalTap = null;
        fixture.Server.RestoreCheckpoint(checkpoint: checkpoint!);
        Assert.Equal(atCheckpoint, Assert.Single(collection: runtime.DescribeCost()));
        foreach (var entry in tail) { Assert.True(condition: fixture.Server.TryApplyJournalTailMutation(engineTick: entry.EngineTick, mutation: entry.Mutation, tick: entry.Tick)); }
        Assert.DoesNotContain(collection: fixture.Server.Definition.State, filter: row => (row.Name.Value == "guest-effect"));
        Assert.Contains(collection: fixture.Server.Definition.State, filter: row => (row.Name.Value == "external-edit"));
        fixture.Step();
        Assert.True(condition: (Assert.Single(collection: runtime.DescribeCost()).LastTickFuelConsumed > atCheckpoint.LastTickFuelConsumed));
    }
    [Fact]
    public void ChangedDeploymentRecoversPinnedGuestMemoryBeforeReconciliation() {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-deploy-");
        using var fixture = Fixtures.FreshServer(Document(directory));
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);

        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); }
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        var expected = Assert.Single(collection: runtime.DescribeCost());

        File.AppendAllText(directory.PathOf(name: "counter.wat"), "\n;; new deployment\n");
        var (recovered, _) = WorldServer.FromCheckpoint(checkpoint!, fixture.Server.Profiles, fixture.Server.Machines, "recovered");
        using var restored = WorldAddonRuntime.Create(definition: recovered.Definition, server: recovered);

        Assert.Equal(expected, Assert.Single(collection: restored.DescribeCost()));
        recovered.Advance(stepTicks: FixedTickConversion.PerRate(ratePerSecond: ((uint)recovered.Definition.SimulationRateHz)));
        Assert.True(condition: (Assert.Single(collection: restored.DescribeCost()).LastTickFuelConsumed > expected.LastTickFuelConsumed));
        // Ordinary authored reconciliation replaces only the changed declaration and starts that guest fresh.
        var bytes = File.ReadAllBytes(path: directory.PathOf(name: "counter.wat"));
        var changed = recovered.Definition.Addons[0] with { Hash = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))) };

        recovered.EnqueueMutation(new WorldMutation.UpsertAddon(Principal.Console, changed));
        Assert.True(condition: recovered.DrainAdministrative());
        Assert.False(condition: restored.AnyEverPumped);
        Assert.Equal(0UL, Assert.Single(collection: restored.DescribeCost()).TotalFuelConsumed);
    }
    [Fact]
    public void MalformedGuestImageRefusesLiveRestoreBeforeAnyAuthorityStateChanges() {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-atomic-");
        using var fixture = Fixtures.FreshServer(Document(directory));
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);

        fixture.Step();
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        fixture.Step();
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(Principal: Principal.Console,
            Row: new WorldStateRow(CellName.Parse(candidate: "after-checkpoint"), CellKind.Int)));
        Assert.True(condition: fixture.Server.DrainAdministrative());
        var definition = fixture.Server.Definition;
        var tick = fixture.Server.CompletedTick;
        var cost = Assert.Single(collection: runtime.DescribeCost());

        Assert.Throws<InvalidDataException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: checkpoint! with { Addons = "null"u8.ToArray() }));
        Assert.Throws<InvalidDataException>(testCode: () => fixture.Server.Persistence.RestoreReplayStart(checkpoint! with { Addons = "null"u8.ToArray() }));
        Assert.Same(definition, fixture.Server.Definition);
        Assert.Equal(tick, fixture.Server.CompletedTick);
        Assert.Equal(cost, Assert.Single(collection: runtime.DescribeCost()));
        fixture.Step();
        Assert.Equal((tick + 1), fixture.Server.CompletedTick);
    }
    [Fact]
    public void HistorySeekRestoresTheLiveGuestsPrivateCounter() {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-history-");
        using var fixture = Fixtures.FreshServer(Document(directory));
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);
        var tape = Tape(directory: directory, fixture: fixture);
        var history = new WorldHistory(fixture.Server, tape, [], Fixtures.MachineHostFactory, new WorldStateRoot(path: directory.RootPath));

        Assert.True(condition: history.TryOn(windowSeconds: WorldHistory.DefaultWindowSeconds, budgetBytes: WorldHistory.DefaultBudgetBytes, refusal: out var reason), userMessage: reason);
        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); tape.NoteTick(); }
        var saved = Assert.Single(collection: runtime.DescribeCost());

        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); tape.NoteTick(); }
        Assert.True(condition: history.TrySeek(documentPath: null, refusal: out reason, report: out var report, target: 5), userMessage: reason);
        Assert.True(condition: report.Matches);
        Assert.Equal(saved, Assert.Single(collection: runtime.DescribeCost()));
    }
}
