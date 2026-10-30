using Puck.Testing;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A scenario's terminal tape and input mask never cross the next scenario's boot boundary.</summary>
public sealed class ScenarioResetLawTests {
    [Fact]
    public void ScenarioDeliveryReadsTheRestoredClockSeeds() {
        var row = new WorldStateRow(CellName.Parse(candidate: "phase"), CellKind.Fixed,
            Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Fixed(rawBits: 16384))]);
        var definition = Fixtures.BuildDocument().WithWorldState(rows: [row]) with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "clock", State: "phase")]),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var server = fixture.Server;

        Assert.True(condition: server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            checkpoint: out var boot, reason: out var reason), userMessage: reason);
        var expected = server.ClockSeeds["clock"];

        server.ResetClockSeeds(definition: definition.WithWorldState(rows: [row with {
            Cells = [new StateCell(WorldStateRow.SlotKey, CellValue.Fixed(rawBits: 49152))],
        }]));
        Assert.NotEqual(expected, server.ClockSeeds["clock"]);
        var sink = new SeedSink(seed: () => server.ClockSeeds["clock"]);
        using var attached = server.AttachSink(sink: sink);

        sink.Seeds.Clear();

        server.Persistence.RestoreScenario(checkpoint: boot!);

        Assert.Equal(expected: [expected], actual: sink.Seeds);
    }

    private sealed class SeedSink(Func<ulong> seed) : IClientSink {
        public List<ulong> Seeds { get; } = [];

        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) => Seeds.Add(item: seed());
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverSessionLever(WorldSessionLever lever) { }
        public void DeliverSnapshot(in WorldSnapshot snapshot) { }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) { }
    }

    [Fact]
    public void AProbeRetainsInputAfterTheProducerReusesItsBorrowedBuffers() {
        using var run = new WorldHistoryHarness(on: false);
        var submission = Fixtures.Input(server: run.Fixture.Server, link: run.Transport, body: 0,
            image: run.Channels.RoleOrdinals.Intent(moveAdvance: Puck.Maths.FixedQ4816.One),
            actor: Principal.Seat(slot: 0), tick: run.Fixture.Server.NextInputTick);
        var contributions = submission.Input.Contributions.ToArray();
        var events = submission.Input.Events.ToArray();

        Assert.NotEmpty(collection: contributions);
        Assert.NotEmpty(collection: events);
        submission = submission with { Input = submission.Input with { Contributions = contributions, Events = events } };
        using var probe = run.Tape.ObserveReplay();

        run.Transport.SubmitIntent(submission: submission);
        run.StepWithoutInput();
        Array.Clear(array: contributions);
        Array.Clear(array: events);
        run.StepWithoutInput();
        probe.Dispose();
        Assert.Equal(-1, probe.Verify());
    }
    [InlineData(0)]
    [InlineData(1)]
    [Theory]
    public void AProbePreservesAppliedInputAtItsFinalBetweenTickBoundary(int ticks) {
        using var run = new WorldHistoryHarness(on: false);
        using var probe = run.Tape.ObserveReplay();

        run.Steps(count: ticks);
        var before = run.Hash();
        var applied = false;

        run.Transport.SubmitSession(new SessionRequest.Leave(Principal: Principal.Seat(slot: 0), Slot: 0),
            reply => { Assert.True(condition: reply.Accepted, userMessage: reply.Reason); applied = true; });
        Assert.True(condition: applied);
        Assert.NotEqual(before, run.Hash());
        probe.Dispose();
        Assert.Equal(-1, probe.Verify());
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ACompanionProbeKeepsCapturingAfterTheOperatorRecordingEnds(bool cancel) {
        using var run = new WorldHistoryHarness(on: false);
        using var companion = HostRow.Build("companion");

        Assert.True(condition: run.Tape.TryBeginRecording(name: "short", refusal: out var reason), userMessage: reason);
        Assert.True(condition: run.Tape.TryRecordCompanion(companion.Instance, out reason), userMessage: reason);
        using var probe = run.Tape.ObserveReplay(instance: companion.Instance);
        var tape = companion.Instance.Tape!;

        run.Steps(count: 1);
        companion.Server.Advance(stepTicks: Fixtures.StepTicks);
        tape.NoteTick();

        if (cancel) { _ = run.Tape.CancelRecording(); } else { _ = run.Tape.StopRecording(); }
        Assert.Same(tape, companion.Instance.Tape);
        companion.Server.Advance(stepTicks: Fixtures.StepTicks);
        tape.NoteTick();
        probe.Dispose();
        Assert.Equal(-1, probe.Verify());
    }
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
        Assert.Equal(-1, earlier.Verify());
        // A committed departure has no rollback to consume its replay-only body. Restoring the start must
        // discard that previous re-drive before the same source-scoped transfer id can be used again.
        Assert.Equal(-1, earlier.Verify());
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
            admitsSpawn: true,
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

        Assert.True(condition: host.TryStart(instance: out var visitor, name: "visitor", path: source, reason: out reason), userMessage: reason);
        var firstAuthority = visitor!.Server.AuthorityIdentity;

        Assert.True(condition: host.TryStop(name: "visitor", reason: out reason), userMessage: reason);
        Assert.True(condition: host.TryStart(instance: out visitor, name: "visitor", path: source, reason: out reason), userMessage: reason);
        Assert.NotEqual(firstAuthority, visitor!.Server.AuthorityIdentity);

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
        Assert.True(condition: host.TryStart(instance: out visitor, name: "visitor", path: source, reason: out reason), userMessage: reason);
        Assert.Equal(firstAuthority, visitor!.Server.AuthorityIdentity);
        Assert.True(condition: host.TryStop(name: "visitor", reason: out reason), userMessage: reason);
        run.StepWithoutInput();
        var afterDrive = run.Hash();

        host.RestoreScenario(checkpoint: boot!);
        run.StepWithoutInput();
        Assert.Equal(afterDrive, run.Hash());
    }
}
