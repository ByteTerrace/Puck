using System.Security.Cryptography;
using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Addons;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A late recording restarts guest memory at its checkpoint boundary; a refused arm preserves it.
/// The guest's increasing loop cost witnesses its private global without exporting or checkpointing that global.</summary>
public sealed class ReplayAddonRestartLawTests {
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

    private static WorldDefinition Document(TemporaryDirectory directory) {
        var bytes = Encoding.UTF8.GetBytes(s: Counter);
        var path = directory.PathOf(name: "counter.wat").Replace(newChar: '/', oldChar: '\\');

        File.WriteAllBytes(bytes: bytes, path: path);
        return Fixtures.BuildDocument() with {
            ChannelsRaw = [.. Fixtures.BuildDocument().Channels, new("held", ChannelShape.Binary, Composition: true)],
            AddonsRaw = [new WorldAddonRow("counter", path,
                ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))), 1000000UL, true)],
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
    public void ALateArmRestartsGuestMemoryAndReDrivesFromItsCheckpoint() {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-arm-");
        using var fixture = Fixtures.FreshServer(Document(directory: directory));

        fixture.JoinSeat();
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);
        var tape = Tape(directory: directory, fixture: fixture);

        fixture.Step();
        var firstFuel = Assert.Single(collection: runtime.DescribeCost()).LastTickFuelConsumed;
        var actor = Principal.Addon(name: "counter");

        fixture.Server.Grant(new(actor, WorldCapability.Drive, GrantSubject.Body(index: 0), false, Budget: 8,
            Reach: new(["held"])), Principal.Console);
        Assert.True(condition: fixture.Server.Body(index: 0)!.Channels.TryGetOrdinal(name: "held", ordinal: out var ordinal));
        var intent = default(PlayerIntent).WithChannel(ordinal: ordinal, value: FixedQ4816.One);

        Assert.True(condition: fixture.Server.Input.TryPublishSnapshot(actor, 0, "guest", intent, fixture.Server.Input.Interval.Tick,
            out var session, out var batch, out var inputReason), userMessage: inputReason);
        Assert.True(condition: fixture.Server.Input.TryAdmit(session, batch, out inputReason), userMessage: inputReason);
        for (var tick = 0; (tick < 4); tick++) { fixture.Step(); }
        Assert.True(condition: (Assert.Single(collection: runtime.DescribeCost()).LastTickFuelConsumed > firstFuel));
        // An ordinary checkpoint preserves private memory; an operator recording still explicitly restarts guests.
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var captureReason), userMessage: captureReason);
        Assert.NotEmpty(collection: checkpoint!.Addons!);
        Assert.True(condition: tape.TryBeginRecording(name: "late-addon", refusal: out var refusal), userMessage: refusal);
        Assert.False(condition: runtime.AnyEverPumped);
        Assert.Equal(0UL, Assert.Single(collection: runtime.DescribeCost()).TotalFuelConsumed);
        fixture.Step();
        tape.NoteTick();
        Assert.Equal(firstFuel, Assert.Single(collection: runtime.DescribeCost()).LastTickFuelConsumed);
        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); tape.NoteTick(); }
        var stop = tape.StopRecording();

        Assert.Null(@object: stop.VerifyFault);
        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());
        using var stream = File.OpenRead(path: stop.Path);

        Assert.Equal(5UL, WorldReplaySnapshot.Read(stream: stream).StartTick);
        Assert.True(condition: tape.Verify(name: "late-addon").Passing);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ARefusedArmLeavesTheRunningGuestMemoryIntact(bool changedModule) {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-arm-refusal-");
        using var fixture = Fixtures.FreshServer(Document(directory: directory));

        fixture.JoinSeat();
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);
        var tape = Tape(directory: directory, fixture: fixture);

        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); }
        var before = Assert.Single(collection: runtime.DescribeCost());

        if (changedModule) {
            File.AppendAllText(directory.PathOf(name: "counter.wat"), "\n;; changed after the document pinned it\n");
        } else {
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(
                Row: new WorldStateRow(Name: CellName.Parse(candidate: "pending"), Kind: CellKind.Int),
                Principal: Principal.Console
            ));
        }
        Assert.False(condition: tape.TryBeginRecording(name: "refused-addon", refusal: out var refusal));
        Assert.StartsWith(actualString: refusal, comparisonType: StringComparison.Ordinal, expectedStartString: "StartNotCheckpointable:");
        Assert.Equal(WorldReplayMode.Idle, tape.Mode);
        Assert.Equal(before, Assert.Single(collection: runtime.DescribeCost()));
        Assert.True(condition: runtime.AnyEverPumped);
        Assert.Null(@object: fixture.Server.MutationTap);
        fixture.Step();
        Assert.True(condition: (Assert.Single(collection: runtime.DescribeCost()).LastTickFuelConsumed > before.LastTickFuelConsumed));
    }
    [Fact]
    public void AnIndependentCaptureReplaysAnOperatorArmAndItsGuestRestart() {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-probe-");
        using var fixture = Fixtures.FreshServer(Document(directory: directory));

        fixture.JoinSeat();
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);
        var tape = Tape(directory: directory, fixture: fixture);
        using var probe = tape.ObserveReplay();

        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); tape.NoteTick(); }
        Assert.True(condition: tape.TryBeginRecording(name: "inside-probe", refusal: out var refusal), userMessage: refusal);
        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); tape.NoteTick(); }
        var stopped = tape.StopRecording();

        Assert.Null(@object: stopped.VerifyFault);
        Assert.True(condition: stopped.Verdict!.Passing, userMessage: stopped.Verdict.Describe());
        Assert.Equal(-1, probe.Verify());
    }
}
