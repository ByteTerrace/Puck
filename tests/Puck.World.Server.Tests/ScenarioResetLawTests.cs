using Puck.Testing;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A scenario's terminal tape and input mask never cross the next scenario's boot boundary.</summary>
public sealed class ScenarioResetLawTests {
    [Fact]
    public void AProbeStartingAfterATransferDoesNotReplayTheAlreadyAppliedDeparture() {
        using var directory = new TemporaryDirectory(prefix: "puck-probe-transfer-");
        using var source = HostRow.Build("source");
        using var target = HostRow.Build("target");
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: directory.RootPath));

        host.Admit(row: source.Instance);
        host.Admit(row: target.Instance);
        for (var slot = 0; (slot < 2); slot++) {
            Assert.True(condition: source.Server.ApplySession(request: new SessionRequest.Join(
                Principal: Principal.Seat(slot: slot), Slot: slot, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        }
        var tape = new WorldReplayTape(source.Server, source.Server.Profiles, ((LoopbackTransport)source.Instance.Link),
            [], Fixtures.MachineHostFactory, static (_, _) => new NullAddonHost(), new WorldStateRoot(path: directory.RootPath));

        source.Instance.Tape = tape;
        using var earlier = tape.ObserveReplay();

        _ = host.EnqueueTransfer(actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "target"),
            scope: WorldInstanceHost.TransferScope.Body, sourceInstance: "source", sourceSlot: 0);
        host.DrainPendingTransfers();
        Assert.False(condition: source.Server.Population.IsActive(index: 0));
        Assert.True(condition: target.Server.Population.IsActive(index: 0));
        using var later = tape.ObserveReplay();

        host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        later.Dispose();
        earlier.Dispose();
        Assert.Equal(-1, later.Verify());
    }
    [Fact]
    public void ADriveStillRunningAtExportIsCancelledBeforeTheNextScenario() {
        using var directory = new TemporaryDirectory(prefix: "puck-scenario-drive-");
        using var run = new WorldHistoryHarness(on: false);
        var server = run.Fixture.Server;
        var source = directory.PathOf(name: "boot.json");

        File.WriteAllBytes(source, WorldDefinitionSerialization.Serialize(definition: server.Definition));
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: directory.RootPath));
        var instance = new WorldInstance("boot", () => source, server, null, run.Transport,
            new WorldFederationIdentity(new InertAuthenticator(), server.AuthorityIdentity), new WorldFileOrigin(source)) { Tape = run.Tape };

        host.AdmitBoot(row: instance);
        host.CaptureScenarioBoot();
        Assert.True(condition: server.TryCaptureCheckpoint(host.CaptureRow(row: instance), out var boot, out var reason), userMessage: reason);

        Assert.True(condition: run.Tape.TryBeginRecording(name: "long", refusal: out reason), userMessage: reason);
        run.Steps(count: 20);
        _ = run.Tape.StopRecording();
        // replay.drive is the console's live-play verb. Export after two of its twenty recorded ticks.
        var inspector = new WorldReplayInspector(server.Profiles, [], Fixtures.MachineHostFactory, static (_, _) => new NullAddonHost());
        var commands = new CommandRegistry([new WorldReplayCommandModule(run.Tape, inspector, host)]);
        var play = commands.Submit(line: "replay.drive long");

        Assert.False(condition: play.IsError, userMessage: play.Output);
        for (var tick = 0; (tick < 2); tick++) {
            run.Tape.InjectDriveTick();
            run.StepWithoutInput();
        }
        Assert.Equal(WorldReplayMode.Replaying, run.Tape.Mode);
        Assert.True(condition: run.Transport.InputMasked);

        host.RestoreScenario(checkpoint: boot!);
        Assert.Equal(WorldReplayMode.Idle, run.Tape.Mode);
        Assert.False(condition: run.Transport.InputMasked);
        run.StepWithoutInput();
        var afterDrive = run.Hash();

        host.RestoreScenario(checkpoint: boot!);
        run.StepWithoutInput();
        Assert.Equal(afterDrive, run.Hash());
    }
}
