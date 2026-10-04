using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
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
    // A paused machine still synchronizes its bindings, so its memo and the cells it wrote are state a step reached:
    // a recording armed after them starts from a checkpoint that holds them, and re-drives to every recorded state,
    // with the bindings live and after they are removed.
    [Fact]
    public void ARecordingArmedAfterAPausedMachineObservedItsBindingsReDrivesFromItsCheckpoint() {
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
        Assert.True(condition: harness.Tape.TryBeginRecording(name: "observed", refusal: out refusal), userMessage: refusal);
        harness.StepWithoutInput();
        harness.StepWithoutInput();
        var observed = harness.Tape.StopRecording();

        Assert.True(condition: observed.Verdict!.Passing, userMessage: observed.Verdict.Describe());

        harness.Submit(mutation: new WorldMutation.UpsertMachine(
            Principal: Principal.Console,
            Machine: definition.Machines[0] with { Memory = null }
        ));
        harness.StepWithoutInput();
        Assert.Null(@object: server.MachineBindingState(binding: "send", machine: Machine));
        Assert.True(condition: harness.Tape.TryBeginRecording(name: "removed", refusal: out refusal), userMessage: refusal);
        harness.StepWithoutInput();
        harness.StepWithoutInput();
        var removed = harness.Tape.StopRecording();

        Assert.True(condition: removed.Verdict!.Passing, userMessage: removed.Verdict.Describe());
    }
}
