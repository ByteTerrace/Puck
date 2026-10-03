using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: arming never produces a tape that cannot re-drive. A recording armed before the world's first
/// step starts from the definition's boot image and the seats; one armed later starts from a checkpoint of the live
/// state taken at the arm, so it re-drives clean from its first tick; and a state no checkpoint captures refuses the arm
/// by name and arms nothing. The red leg proves the start is real: the same tape restored from a checkpoint taken at the
/// wrong tick reports MISMATCH at its first tick.
/// </summary>
public sealed class ReplayArmingLawTests {
    private static WorldReplaySnapshot ReadTape(string path) {
        using var stream = File.OpenRead(path: path);

        return WorldReplaySnapshot.Read(stream: stream);
    }

    [Fact]
    public void AMidRunArmReDrivesCleanFromItsFirstTick() {
        using var harness = new WorldHistoryHarness(on: false, seats: 1);

        harness.Steps(count: 20);
        Assert.True(condition: harness.Tape.TryBeginRecording(name: "mid-run", refusal: out var refusal), userMessage: refusal);
        harness.Steps(count: 30);

        var stop = harness.Tape.StopRecording();

        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());
        Assert.Equal(expected: 20UL, actual: ReadTape(path: stop.Path).StartTick);
        Assert.True(condition: harness.Tape.Verify(name: "mid-run").Passing);
    }
    // The history keeps the capture attached, so input submitted for the next tick before the arm is already in the
    // open tick when the recording starts. That input already reached the server, where the start checkpoint holds it,
    // so the recording's first tick must not carry it a second time.
    [Fact]
    public void AnArmWhileTheHistoryCapturesTapesTheOpenTicksInputOnce() {
        using var harness = new WorldHistoryHarness(on: true, seats: 1);

        harness.Steps(count: 12);
        harness.Submit(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(Kind: CellKind.Int, Name: CellName.Parse(candidate: "armed"))
        ));
        harness.Steps(count: 1);
        harness.Transport.SubmitIntent(submission: new IntentSubmission(
            EntityIndex: 0,
            Intent: harness.Channels.RoleOrdinals.Intent(
                moveAdvance: Puck.Maths.FixedQ4816.One,
                moveStrafe: Puck.Maths.FixedQ4816.Zero,
                turn: Puck.Maths.FixedQ4816.Zero
            ),
            Principal: Principal.Seat(slot: 0),
            Tick: harness.Fixture.Server.NextInputTick
        ));
        Assert.True(condition: harness.Tape.TryBeginRecording(name: "open-tick", refusal: out var refusal), userMessage: refusal);
        harness.StepWithoutInput();
        harness.Steps(count: 20);

        var stop = harness.Tape.StopRecording();

        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());
    }
    [Fact]
    public void ATapeWhoseCheckpointWasTakenAtTheWrongTickReportsMismatchAtItsFirstTick() {
        using var harness = new WorldHistoryHarness(on: false, seats: 1);

        harness.Steps(count: 10);
        Assert.True(
            condition: harness.Fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var early,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var reason
            ),
            userMessage: reason
        );
        harness.Steps(count: 5);
        Assert.True(condition: harness.Tape.TryBeginRecording(name: "right", refusal: out var refusal), userMessage: refusal);
        harness.Steps(count: 10);

        var stop = harness.Tape.StopRecording();

        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());

        var right = ReadTape(path: stop.Path);

        Assert.Equal(expected: 15UL, actual: right.StartTick);
        WorldReplaySnapshot.WriteFile(
            path: harness.Tape.PathFor(name: "wrong"),
            recording: new WorldReplaySnapshot {
                Authority = right.Authority,
                DefinitionJson = right.DefinitionJson,
                DocumentDirectory = right.DocumentDirectory,
                DocumentPath = right.DocumentPath,
                Instance = right.Instance,
                MountedAddons = right.MountedAddons,
                PipelineSourceDirectory = right.PipelineSourceDirectory,
                RecordedAuthoritativeHashes = right.RecordedAuthoritativeHashes,
                RecordedHashes = right.RecordedHashes,
                Seats = right.Seats,
                SimulationRate = right.SimulationRate,
                StartCheckpoint = WorldReplaySnapshot.ForTape(checkpoint: early!),
                Ticks = right.Ticks,
            }
        );

        var wrong = harness.Tape.Verify(name: "wrong");

        Assert.False(condition: wrong.Passing);
        Assert.Equal(expected: 0, actual: wrong.Primary.DivergedAt);
    }
    // An edit submitted but not yet applied is state no checkpoint captures: the arm refuses by name and leaves the
    // tape idle, with nothing tapped.
    [Fact]
    public void AStateNoCheckpointCapturesRefusesTheArmByName() {
        using var harness = new WorldHistoryHarness(on: false, seats: 1);

        harness.Steps(count: 5);
        harness.Submit(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(Kind: CellKind.Int, Name: CellName.Parse(candidate: "pending"))
        ));

        Assert.False(condition: harness.Tape.TryBeginRecording(name: "pending", refusal: out var refusal));
        Assert.StartsWith(actualString: refusal, comparisonType: StringComparison.Ordinal, expectedStartString: "StartNotCheckpointable:");
        Assert.Equal(expected: WorldReplayMode.Idle, actual: harness.Tape.Mode);
        Assert.Null(@object: harness.Fixture.Server.MutationTap);
        Assert.False(condition: harness.Fixture.Server.Profiles.Recording);
    }
    [Fact]
    public void ARecordingArmedBeforeTheFirstStepReDrivesToEveryRecordedState() {
        using var directory = new TemporaryDirectory(prefix: "puck-replay-arming-early-");
        using var fixture = Fixtures.FreshServer();
        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: fixture.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: fixture.Server.Profiles,
            stateRoot: new WorldStateRoot(path: directory.RootPath),
            transport: new LoopbackTransport(server: fixture.Server)
        );

        Assert.True(condition: tape.TryBeginRecording(name: "early", refusal: out var refusal), userMessage: refusal);
        for (var tick = 0; (tick < 10); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        var stop = tape.StopRecording();

        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());
        Assert.Null(@object: ReadTape(path: stop.Path).StartCheckpoint);
    }
}
