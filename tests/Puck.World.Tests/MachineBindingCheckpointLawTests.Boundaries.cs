using System.Text.Json;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class MachineBindingCheckpointLawTests {
    [Fact]
    public void ASeekDropsAnObservationAbsentFromItsKeyframeBeforeReplayingTheBindingIntroduction() {
        using var directory = new TemporaryDirectory(prefix: "puck-binding-memo-introduction-");
        var rom = directory.WriteBytes(bytes: ProgramImage(), name: "program.gba");
        var binding = Binding(address: SendAddress, name: "send", row: "send", update: "onChange");
        var definition = Document(rom, binding);

        definition = definition with { MachinesRaw = [definition.Machines[0] with { Memory = null }] };
        using var harness = new WorldHistoryHarness(definition: definition, seats: 0);
        var server = harness.Fixture.Server;

        harness.StepWithoutInput();
        var keyframe = harness.Tick;

        Assert.Equal(expected: ProgramByte, actual: ByteAt(server: server));
        harness.Submit(mutation: new WorldMutation.UpsertMachine(
            Principal: Principal.Console,
            Machine: definition.Machines[0] with { Memory = [binding] }
        ));
        harness.StepWithoutInput();
        var introduced = harness.Tick;
        var image = Image(server: server);

        Assert.Equal(expected: ((ulong)FirstValue), actual: ByteAt(server: server));
        Assert.NotNull(@object: server.MachineBindingState(binding: "send", machine: Machine));
        harness.StepWithoutInput();

        Assert.True(condition: harness.SeekAndProve(target: keyframe).RebuiltDocument);
        Assert.Equal(expected: ProgramByte, actual: ByteAt(server: server));
        var replayed = harness.SeekAndProve(target: introduced);

        Assert.Null(@object: replayed.KeyframeTick);
        Assert.Equal(expected: 1, actual: replayed.TicksResimulated);
        Assert.Equal(expected: ((ulong)FirstValue), actual: ByteAt(server: server));
        Assert.Equal(expected: image, actual: Image(server: server));
    }
    [Fact]
    public void RecordingAPausedMachineRequiresTheWorldsFirstTickEvenAfterItsBindingsAreRemoved() {
        using var directory = new TemporaryDirectory(prefix: "puck-binding-memo-paused-");
        var rom = directory.WriteBytes(bytes: ProgramImage(), name: "program.gba");
        var binding = Binding(address: SendAddress, name: "send", row: "send", update: "onChange") with {
            Direction = WorldMachineMemoryDirection.Read,
            Access = "inspect",
        };
        var definition = Document(rom, binding);

        definition = definition with { MachinesRaw = [definition.Machines[0] with { Running = false }] };
        using var harness = new WorldHistoryHarness(definition: definition, seats: 0, on: false);
        var server = harness.Fixture.Server;

        Assert.True(condition: harness.Tape.TryBeginRecording(name: "fresh", refusal: out var refusal), userMessage: refusal);
        harness.Tape.CancelRecording();
        harness.StepWithoutInput();
        Assert.False(condition: server.AnyMachineEverPumped);
        Assert.Equal(expected: 0L, actual: server.MachineBindingState(binding: "send", machine: Machine)!.Value.LastValue);
        harness.Submit(mutation: SetSend(value: FirstValue));
        harness.StepWithoutInput();
        Assert.Equal(expected: FirstValue, actual: server.Definition.State[0].Cells![0].Value.AsInt);
        Assert.False(condition: harness.Tape.TryBeginRecording(name: "observed", refusal: out refusal));
        Assert.Contains(actualString: refusal, expectedSubstring: "binding memo");

        harness.Submit(mutation: new WorldMutation.UpsertMachine(
            Principal: Principal.Console,
            Machine: definition.Machines[0] with { Memory = null }
        ));
        harness.StepWithoutInput();
        Assert.Null(@object: server.MachineBindingState(binding: "send", machine: Machine));
        Assert.False(condition: harness.Tape.TryBeginRecording(name: "removed", refusal: out refusal));
        Assert.Contains(actualString: refusal, expectedSubstring: "binding memo");
    }
    [InlineData(WorldScreenMemoryDirection.Read)]
    [InlineData(WorldScreenMemoryDirection.Write)]
    [Theory]
    public void ScreenMemoryOnANamedMachineClosesCaptureAndSeekBeforeEitherCanLoseItsMemo(WorldScreenMemoryDirection direction) {
        using var directory = new TemporaryDirectory(prefix: "puck-binding-memo-screen-");
        var image = new byte[0x8000];

        image[0x100] = 0x18;
        image[0x101] = 0xFE;
        var rom = directory.WriteBytes(bytes: image, name: "program.gb");
        var definition = Document(rom, Binding(address: 0xC040, name: "send", row: "send", update: "onChange"));
        var screen = Fixtures.BuildDocument().Screens[0] with {
            Source = new WorldScreenSource.Machine(Instance: Machine, Output: "video"),
            Memory = null,
        };

        definition = definition with {
            ScreensRaw = null,
            MachinesRaw = [new WorldMachine(
                Name: Machine,
                Engine: "gaming-brick",
                Configuration: JsonSerializer.SerializeToElement(new {
                    schema = "puck.gaming-brick.configuration.v1",
                    model = "cgb",
                    boot = "fast",
                    content = new { path = rom },
                }),
                Running: false
            )],
        };
        using var harness = new WorldHistoryHarness(definition: definition, seats: 0);
        var server = harness.Fixture.Server;

        Assert.True(condition: harness.Tape.TryBeginRecording(name: "boot", refusal: out var refusal), userMessage: refusal);
        harness.StepWithoutInput();
        _ = harness.Tape.StopRecording();
        var keyframe = harness.Tick;

        Assert.True(condition: server.TryCaptureCheckpoint(
            hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out _, reason: out var reason
        ), userMessage: reason);
        Assert.False(condition: server.AnyScreenOpEverApplied);
        harness.Submit(mutation: new WorldMutation.UpsertScreen(
            Principal: Principal.Console,
            Screen: screen with { Memory = [new WorldScreenMemory(Address: 0xC040, Width: 1, Row: "send", Direction: direction)] }
        ));
        harness.StepWithoutInput();

        Assert.True(condition: server.TryMachineMemoryObserved(screen: screen.Index, address: 0xC040, direction: direction, value: out _));
        Assert.False(condition: server.AnyMachineEverPumped);
        Assert.False(condition: server.TryCaptureCheckpoint(
            hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out _, reason: out reason
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "screen");
        var before = server.Definition;
        var tick = harness.Tick;
        var runtime = Image(server: server);

        Assert.False(condition: harness.History.TrySeek(
            documentPath: null, refusal: out reason, report: out _, target: keyframe
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "screen");
        Assert.Same(expected: before, actual: server.Definition);
        Assert.Equal(expected: tick, actual: harness.Tick);
        Assert.Equal(expected: runtime, actual: Image(server: server));
        Assert.False(condition: harness.Tape.TryBeginDrive(
            documentPath: null, forkName: null, name: "boot", refusal: out reason, toTick: null
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "screen");
        Assert.Same(expected: before, actual: server.Definition);
        Assert.Equal(expected: tick, actual: harness.Tick);
        Assert.Equal(expected: runtime, actual: Image(server: server));
    }
}
