using System.Collections.Immutable;
using System.Numerics;
using Puck.Commands;
using Puck.Demo.Replay;

namespace Puck.Demo.MiniAction;

/// <summary>
/// The day-one determinism + replay self-check, pure CPU (the simulation has no GPU dependency) — exercised through
/// roster CHURN. It scripts a join/leave schedule (fill to capacity, two joins on one tick, a mid-slot leave, and a
/// recycling rejoin) plus scripted per-player input expressed as the engine's command lanes, runs it twice and asserts
/// the two per-tick state-hash sequences are identical (determinism), then records ONE run through the engine snapshot
/// recorder (a <see cref="SnapshotRecording"/> for input + a roster channel, bundled as a <see cref="MiniActionRecording"/>),
/// round-trips it through the binary format, replays it, and asserts the replayed hashes match (replay fidelity). Input
/// rides the same <see cref="ISnapshotSource"/> seam the live router uses, so this is the engine generalization of the
/// old bespoke intent log.
/// </summary>
public static class MiniActionDeterminism {
    private const float TickSeconds = (1f / 240f); // the host's fixed step; any constant proves determinism

    /// <summary>The outcome of a self-check.</summary>
    public readonly record struct Result(bool Passed, string Message);

    /// <summary>Runs the self-check over <paramref name="ticks"/> ticks with a scripted join/leave schedule.</summary>
    public static Result Run(int ticks = 1200) {
        const uint seed = 0x00C0FFEEu;

        var registry = new CommandRegistry(modules: [new MiniActionCommandModule()]);

        if (!registry.TryGetId(name: MiniActionInput.MoveCommand, id: out var moveId) ||
            !registry.TryGetId(name: MiniActionInput.JumpCommand, id: out var jumpId)) {
            return new Result(Message: "MiniAction commands are not interned.", Passed: false);
        }

        var players = new Guid[MiniActionWorld.MaxPlayers];

        for (var index = 0; (index < players.Length); index++) {
            players[index] = DeterministicGuid(salt: (uint)index);
        }

        var schedule = BuildSchedule(players: players);

        // The scripted input: each slot circles its stick at its own phase and jumps periodically, as command lanes.
        ISnapshotSource Script() => new ScriptedSnapshotSource(script: tick => BuildSnapshot(tick: tick, moveId: moveId, jumpId: jumpId));

        // First run records its input snapshots and the per-tick roster events it applied.
        var recorder = new InputRecorder(seed: seed);
        var capturedRoster = new List<ImmutableArray<RosterEvent>>();
        var first = Simulate(seed: seed, input: new RecordingSnapshotSource(inner: Script(), recorder: recorder), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, moveId: moveId, jumpId: jumpId, capturedRoster: capturedRoster);
        var second = Simulate(seed: seed, input: Script(), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, moveId: moveId, jumpId: jumpId, capturedRoster: null);
        var divergence = HashTrace.FirstDivergence(left: first, right: second);

        if (divergence >= 0) {
            return new Result(Message: $"NON-DETERMINISTIC across join/leave: two identical scripted runs diverged at tick {divergence}.", Passed: false);
        }

        // Replay fidelity, including a binary round-trip of the recording (input snapshots + roster events).
        var recording = new MiniActionRecording { Input = recorder.ToRecording(), RosterEvents = [.. capturedRoster], };

        using var stream = new MemoryStream();

        recording.Write(stream: stream, registry: registry);
        stream.Position = 0L;

        var roundTripped = MiniActionRecording.Read(stream: stream, registry: registry);
        var replayed = Simulate(seed: roundTripped.Input.Seed, input: new ReplaySnapshotSource(recording: roundTripped.Input), roster: new ReplayRosterEventSource(recording: roundTripped), ticks: ticks, moveId: moveId, jumpId: jumpId, capturedRoster: null);
        var replayDivergence = HashTrace.FirstDivergence(left: first, right: replayed);

        if (replayDivergence >= 0) {
            return new Result(Message: $"REPLAY DIVERGED from the recorded run at tick {replayDivergence}.", Passed: false);
        }

        // Planet-scale translation invariance: the SAME scripted run with the whole room placed in a far world cell must
        // produce an identical CELL-INVARIANT (local) trajectory — proving the cell+offset coordinate adds astronomical
        // range without perturbing the simulation. The far cell is ~1e9 cells (≈1e15 world units) from the origin.
        const long farCell = 1_000_000_000L;
        var nearLocal = Simulate(seed: seed, input: Script(), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, moveId: moveId, jumpId: jumpId, capturedRoster: null, localHash: true);
        var farLocal = Simulate(seed: seed, input: Script(), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, moveId: moveId, jumpId: jumpId, capturedRoster: null, spawnCell: farCell, localHash: true);
        var cellDivergence = HashTrace.FirstDivergence(left: nearLocal, right: farLocal);

        if (cellDivergence >= 0) {
            return new Result(Message: $"PLANET-SCALE INVARIANCE BROKEN: the cell 0 and cell {farCell:N0} local trajectories diverged at tick {cellDivergence}.", Passed: false);
        }

        return new Result(Message: $"determinism + replay + planet-scale cell-invariance (cell 0 ≡ cell {farCell:N0}) verified over {ticks} ticks with scheduled join/leave/recycle (peak {MiniActionWorld.MaxPlayers} players; final hash 0x{first[^1]:X16})", Passed: true);
    }

    // Exercises empty→full, two joins on one tick (ordering), a mid-slot leave, and a recycling rejoin (the case a naive
    // append-only or FIFO free-list would break).
    private static IReadOnlyList<(ulong Tick, RosterEvent Event)> BuildSchedule(Guid[] players) {
        return [
            (0u,   new RosterEvent(Kind: RosterEventKind.Join,  PlayerId: players[0], Slot: 0)),
            (120u, new RosterEvent(Kind: RosterEventKind.Join,  PlayerId: players[1], Slot: 1)),
            (300u, new RosterEvent(Kind: RosterEventKind.Join,  PlayerId: players[2], Slot: 2)),
            (300u, new RosterEvent(Kind: RosterEventKind.Join,  PlayerId: players[3], Slot: 3)),
            (600u, new RosterEvent(Kind: RosterEventKind.Leave, PlayerId: players[1], Slot: 1)),
            (720u, new RosterEvent(Kind: RosterEventKind.Join,  PlayerId: players[1], Slot: 1)),
        ];
    }

    // One tick's scripted input as command lanes — a lane per slot (free-slot lanes are simply ignored by the sim). Each
    // slot circles its stick at its own phase and jumps for 8 ticks every 90: a press edge, a held window, a release edge.
    private static CommandSnapshot BuildSnapshot(ulong tick, ushort moveId, ushort jumpId) {
        var lanes = ImmutableArray.CreateBuilder<CommandLane>(initialCapacity: MiniActionWorld.MaxPlayers);

        for (var slot = 0; (slot < MiniActionWorld.MaxPlayers); slot++) {
            var angle = ((tick * 0.05f) + (slot * 1.7f));
            var cycle = (int)((tick + ((ulong)(slot * 13))) % 90u);
            var entries = ImmutableArray.CreateBuilder<CommandEntry>(initialCapacity: 2);

            entries.Add(item: new CommandEntry(CommandId: moveId, Value: CommandValue.Axis(value: new Vector2(x: MathF.Cos(x: angle), y: MathF.Sin(x: angle))), Phase: CommandPhase.Active));

            if (cycle == 0) {
                entries.Add(item: new CommandEntry(CommandId: jumpId, Value: CommandValue.Digital(active: true), Phase: CommandPhase.Started));
            } else if (cycle < 8) {
                entries.Add(item: new CommandEntry(CommandId: jumpId, Value: CommandValue.Digital(active: true), Phase: CommandPhase.Active));
            } else if (cycle == 8) {
                entries.Add(item: new CommandEntry(CommandId: jumpId, Value: CommandValue.Digital(active: false), Phase: CommandPhase.Completed));
            }

            entries.Sort(comparison: static (left, right) => left.CommandId.CompareTo(value: right.CommandId));
            lanes.Add(item: new CommandLane(Entries: entries.DrainToImmutable(), Slot: slot));
        }

        return new CommandSnapshot(Lanes: lanes.DrainToImmutable(), Tick: tick);
    }

    private static ulong[] Simulate(uint seed, ISnapshotSource input, IRosterEventSource roster, int ticks, ushort moveId, ushort jumpId, List<ImmutableArray<RosterEvent>>? capturedRoster, long spawnCell = 0L, bool localHash = false) {
        var world = new MiniActionWorld(room: MiniActionRoom.Default, tuning: PlatformerTuning.Default, tickSeconds: TickSeconds, seed: seed, spawnCellX: spawnCell, spawnCellZ: spawnCell);
        var hashes = new ulong[ticks];

        for (var index = 0; (index < ticks); index++) {
            var tick = world.CurrentTick;
            var applied = ImmutableArray.CreateBuilder<RosterEvent>();

            // Roster events FIRST, so a joiner is present (and a leaver gone) for this tick's input + step. Capture the
            // RESOLVED slot, so the recording is a faithful log a replay can cross-check.
            foreach (var rosterEvent in roster.EventsForTick(tick: tick)) {
                var slot = ((rosterEvent.Kind == RosterEventKind.Join)
                    ? world.AddPlayer(playerId: rosterEvent.PlayerId)
                    : world.RemovePlayer(playerId: rosterEvent.PlayerId));

                applied.Add(item: (rosterEvent with { Slot = slot }));
            }

            capturedRoster?.Add(item: applied.DrainToImmutable());

            var snapshot = input.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue);
            var intents = MiniActionSnapshotProjection.ToIntents(snapshot: in snapshot, moveId: moveId, hasMove: true, jumpId: jumpId, hasJump: true);

            world.Advance(intentsBySlot: intents);

            hashes[index] = (localHash ? world.LocalStateHash() : world.StateHash());
        }

        return hashes;
    }
    private static Guid DeterministicGuid(uint salt) {
        var bytes = new byte[16];

        BitConverter.TryWriteBytes(destination: bytes, value: (0xA571_0000u | salt));

        return new Guid(b: bytes);
    }
}
