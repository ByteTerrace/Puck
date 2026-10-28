using Puck.Testing;
using Puck.Commands;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A scenario's terminal tape and input mask never cross the next scenario's boot boundary.</summary>
public sealed class ScenarioResetLawTests {
    [Fact]
    public void ADriveStillRunningAtExportIsCancelledBeforeTheNextScenario() {
        using var directory = new TemporaryDirectory(prefix: "puck-scenario-drive-");
        using var run = new WorldHistoryHarness(on: false);
        var server = run.Fixture.Server;
        var source = directory.PathOf("boot.json");

        File.WriteAllBytes(source, WorldDefinitionSerialization.Serialize(server.Definition));
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(directory.RootPath));
        var instance = new WorldInstance("boot", () => source, server, null, run.Transport,
            new WorldFederationIdentity(new InertAuthenticator(), server.AuthorityIdentity), new WorldFileOrigin(source)) { Tape = run.Tape };

        host.AdmitBoot(instance);
        host.CaptureScenarioBoot();
        Assert.True(server.TryCaptureCheckpoint(host.CaptureRow(instance), out var boot, out var reason), reason);

        Assert.True(run.Tape.TryBeginRecording("long", out reason), reason);
        run.Steps(20);
        _ = run.Tape.StopRecording();
        // replay.drive is the console's live-play verb. Export after two of its twenty recorded ticks.
        var inspector = new WorldReplayInspector(server.Profiles, [], Fixtures.MachineHostFactory, static (_, _) => new NullAddonHost());
        var commands = new CommandRegistry([new WorldReplayCommandModule(run.Tape, inspector, host)]);
        var play = commands.Submit("replay.drive long");

        Assert.False(play.IsError, play.Output);
        for (var tick = 0; (tick < 2); tick++) {
            run.Tape.InjectDriveTick();
            run.StepWithoutInput();
        }
        Assert.Equal(WorldReplayMode.Replaying, run.Tape.Mode);
        Assert.True(run.Transport.InputMasked);

        host.RestoreScenario(boot!);
        Assert.Equal(WorldReplayMode.Idle, run.Tape.Mode);
        Assert.False(run.Transport.InputMasked);
        run.StepWithoutInput();
        var afterDrive = run.Hash();

        host.RestoreScenario(boot!);
        run.StepWithoutInput();
        Assert.Equal(afterDrive, run.Hash());
    }
}
