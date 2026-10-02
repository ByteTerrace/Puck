using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A live server stepped the way <see cref="WorldServerStepShell"/> steps it — seat intents through the
/// loopback, the step, the journal horizon, then the tape's tick close — with an in-session
/// <see cref="WorldHistory"/> riding the tape's capture, and the authoritative hash the live run reached at every tick
/// recorded independently of the history, so a law compares what the history reproduces against what actually ran.</summary>
internal sealed class WorldHistoryHarness : IDisposable {
    private readonly TemporaryDirectory m_state = new(prefix: "puck-history-");

    private readonly int m_seats;

    private ulong m_random;

    public WorldHistoryHarness(WorldDefinition? definition = null, int seats = 1, ulong seed = 1UL, bool on = true) {
        var catalog = TestHookInstaller.CreateMachineCatalog();

        Fixture = Fixtures.FreshServer(
            definition: definition,
            machineCatalog: catalog
        );
        Transport = new LoopbackTransport(server: Fixture.Server);
        Tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: catalog.Engines.Values,
            liveServer: Fixture.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: Fixture.Server.Profiles,
            stateRoot: new WorldStateRoot(path: m_state.RootPath),
            transport: Transport
        );
        History = new WorldHistory(
            engines: catalog.Engines.Values,
            machineHostFactory: Fixtures.MachineHostFactory,
            server: Fixture.Server,
            stateRoot: new WorldStateRoot(path: m_state.RootPath),
            tape: Tape
        );
        Channels = WorldChannelTable.Compile(channels: Fixture.Server.Definition.Channels);
        m_seats = seats;
        m_random = seed | 1UL;

        for (var slot = 0; (slot < seats); slot++) {
            _ = Fixture.JoinSeat(slot: slot);
        }

        Live[Tick] = Hash();

        if (on) {
            Assert.True(
                condition: History.TryOn(
                    budgetBytes: WorldHistory.DefaultBudgetBytes,
                    refusal: out var refusal
                ),
                userMessage: refusal
            );
        }
    }

    public WorldChannelTable Channels { get; }
    public WorldFixture Fixture { get; }
    public WorldHistory History { get; }

    /// <summary>The authoritative hash the live run reached at each tick, by tick — the last write wins, so after a
    /// seek and resume it describes the timeline that now stands.</summary>
    public Dictionary<ulong, ulong> Live { get; } = [];

    public WorldReplayTape Tape { get; }
    public ulong Tick => (Fixture.Server.NextInputTick - 1UL);
    public LoopbackTransport Transport { get; }

    // A seeded input stream: each seat's stick, in whole sixteenths of full scale, so two harnesses on one seed
    // submit the identical stream.
    private FixedQ4816 NextAxis() {
        m_random ^= (m_random << 13);
        m_random ^= (m_random >> 7);
        m_random ^= (m_random << 17);

        return FixedQ4816.FromRawBits(value: ((((long)(m_random % 33UL)) - 16L) * 4096L));
    }

    public void Dispose() {
        Fixture.Dispose();
        m_state.Dispose();
    }
    public ulong Hash() => WorldStateHashComposition.HashAuthoritative(
        server: Fixture.Server,
        tick: Tick
    );
    /// <summary>Submits one seeded intent per seat for the next tick, then steps it.</summary>
    public void Step() {
        var next = Fixture.Server.NextInputTick;

        for (var slot = 0; (slot < m_seats); slot++) {
            Transport.SubmitIntent(submission: new IntentSubmission(
                EntityIndex: slot,
                Intent: Channels.RoleOrdinals.Intent(
                    moveAdvance: NextAxis(),
                    moveStrafe: NextAxis(),
                    turn: NextAxis()
                ),
                Principal: Principal.Seat(slot: slot),
                Tick: next
            ));
        }

        StepWithoutInput();
    }
    /// <summary>Steps one tick with whatever input is already submitted, the shell's order.</summary>
    public void StepWithoutInput() {
        Fixture.Step();
        Fixture.Server.EnforceJournalDepth();
        Tape.NoteTick();
        Live[Tick] = Hash();
    }
    public void Steps(int count) {
        for (var index = 0; (index < count); index++) {
            Step();
        }
    }
    /// <summary>Submits a mutation through the envelope door the console's own writes take, under a fresh operation
    /// id, so the capture tapes it exactly as it tapes a typed edit.</summary>
    public void Submit(WorldMutation mutation) => _ = Transport.SubmitEnvelope(
        operationId: Guid.NewGuid(),
        payload: new WorldSubmissionPayload.Mutation(Value: mutation),
        principal: Principal.Console
    );
    /// <summary>Seeks and asserts the seek reproduced the live run's own hash at the target.</summary>
    public WorldHistorySeekReport SeekAndProve(ulong target) {
        Assert.True(
            condition: History.TrySeek(
                documentPath: null,
                refusal: out var refusal,
                report: out var report,
                target: target
            ),
            userMessage: refusal
        );
        Assert.True(
            condition: report.Matches,
            userMessage: $"seek to {target} diverged at {report.DivergedAt}"
        );
        Assert.Equal(
            expected: Live[target],
            actual: Hash()
        );
        Assert.Equal(
            expected: target,
            actual: Tick
        );

        return report;
    }
}
