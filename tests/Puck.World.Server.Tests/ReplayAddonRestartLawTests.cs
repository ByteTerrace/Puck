using System.Security.Cryptography;
using System.Text;
using Puck.Commands;
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
        var bytes = Encoding.UTF8.GetBytes(Counter);
        var path = directory.PathOf("counter.wat").Replace('\\', '/');
        File.WriteAllBytes(path, bytes);
        return Fixtures.BuildDocument() with {
            AddonsRaw = [new WorldAddonRow("counter", path,
                "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes)), 1000000UL, true)],
        };
    }

    private static WorldReplayTape Tape(WorldFixture fixture, TemporaryDirectory directory) => new(
        addonHostFactory: static (definition, server) => WorldAddonRuntime.Create(definition, server),
        engines: [],
        liveServer: fixture.Server,
        machineHostFactory: Fixtures.MachineHostFactory,
        profiles: fixture.Server.Profiles,
        stateRoot: new WorldStateRoot(directory.RootPath),
        transport: new LoopbackTransport(fixture.Server)
    );

    [Fact]
    public void ALateArmRestartsGuestMemoryAndReDrivesFromItsCheckpoint() {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-arm-");
        using var fixture = Fixtures.FreshServer(Document(directory));
        fixture.JoinSeat();
        using var runtime = WorldAddonRuntime.Create(fixture.Server.Definition, fixture.Server);
        var tape = Tape(fixture, directory);
        fixture.Step();
        var firstFuel = Assert.Single(runtime.DescribeCost()).LastTickFuelConsumed;
        for (var tick = 0; tick < 4; tick++) { fixture.Step(); }
        Assert.True(Assert.Single(runtime.DescribeCost()).LastTickFuelConsumed > firstFuel);
        // An ordinary checkpoint still cannot claim to hold the guest's private memory.
        Assert.False(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out _, out _));
        Assert.True(tape.TryBeginRecording("late-addon", out var refusal), refusal);
        Assert.False(runtime.AnyEverPumped);
        Assert.Equal(0UL, Assert.Single(runtime.DescribeCost()).TotalFuelConsumed);
        fixture.Step();
        tape.NoteTick();
        Assert.Equal(firstFuel, Assert.Single(runtime.DescribeCost()).LastTickFuelConsumed);
        for (var tick = 0; tick < 5; tick++) { fixture.Step(); tape.NoteTick(); }
        var stop = tape.StopRecording();
        Assert.Null(stop.VerifyFault);
        Assert.True(stop.Verdict!.Passing, stop.Verdict.Describe());
        using var stream = File.OpenRead(stop.Path);
        Assert.Equal(5UL, WorldReplaySnapshot.Read(stream).StartTick);
        Assert.True(tape.Verify("late-addon").Passing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefusedArmLeavesTheRunningGuestMemoryIntact(bool changedModule) {
        using var directory = new TemporaryDirectory(prefix: "puck-addon-arm-refusal-");
        using var fixture = Fixtures.FreshServer(Document(directory));
        fixture.JoinSeat();
        using var runtime = WorldAddonRuntime.Create(fixture.Server.Definition, fixture.Server);
        var tape = Tape(fixture, directory);
        for (var tick = 0; tick < 5; tick++) { fixture.Step(); }
        var before = Assert.Single(runtime.DescribeCost());
        if (changedModule) {
            File.AppendAllText(directory.PathOf("counter.wat"), "\n;; changed after the document pinned it\n");
        } else {
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(
                Row: new WorldStateRow(Name: CellName.Parse("pending"), Kind: CellKind.Int),
                Principal: Principal.Console
            ));
        }
        Assert.False(tape.TryBeginRecording("refused-addon", out var refusal));
        Assert.StartsWith("StartNotCheckpointable:", refusal, StringComparison.Ordinal);
        Assert.Equal(WorldReplayMode.Idle, tape.Mode);
        Assert.Equal(before, Assert.Single(runtime.DescribeCost()));
        Assert.True(runtime.AnyEverPumped);
        Assert.Null(fixture.Server.MutationTap);
        fixture.Step();
        Assert.True(Assert.Single(runtime.DescribeCost()).LastTickFuelConsumed > before.LastTickFuelConsumed);
    }
}
