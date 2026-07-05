using System.Collections.Immutable;
using System.Numerics;
using Puck.Commands;
using Puck.Demo.Replay;

namespace Puck.Demo.Overworld;

/// <summary>
/// The day-one determinism + replay self-check, pure CPU (the simulation has no GPU dependency). It scripts a
/// join/leave schedule (fill to capacity, two joins on one tick, a mid-slot leave, and a recycling rejoin) plus
/// scripted per-player input as the engine's command lanes (each slot wanders in a circle, jumps periodically, and
/// presses interact + cycle mid-cycle — driving the cabinet insert / eject / cart-cycle path), runs it twice and
/// asserts the two per-tick state-hash sequences are identical (determinism), then records ONE run through the engine
/// snapshot recorder, round-trips the binary, replays it, and asserts the replayed hashes match (replay fidelity), and
/// finally re-runs the whole thing in a far world cell to prove planet-scale translation invariance. Input rides the
/// same <see cref="ISnapshotSource"/> seam the live router uses.
/// </summary>
public static class OverworldDeterminism {
    private const float TickSeconds = (1f / 240f); // the host's fixed step; any constant proves determinism
    // The scripted room: 4 stands (the overworld's cabinets), each starting empty — carts are chosen at the cabinet now.
    private static readonly OverworldRoom ScriptedRoom = OverworldRoom.WithConsolesAndShelf(consoleCount: 4, shelfCount: 1);

    /// <summary>The outcome of a self-check.</summary>
    public readonly record struct Result(bool Passed, string Message);

    /// <summary>Runs the self-check over <paramref name="ticks"/> ticks with a scripted join/leave schedule.</summary>
    public static Result Run(int ticks = 1600) {
        const uint seed = 0x00C0FFEEu;

        var registry = new CommandRegistry(modules: [new OverworldCommandModule()]);

        if (!registry.TryGetId(name: OverworldInput.MoveCommand, id: out var moveId) ||
            !registry.TryGetId(name: OverworldInput.JumpCommand, id: out var jumpId) ||
            !registry.TryGetId(name: OverworldInput.InteractCommand, id: out var interactId) ||
            !registry.TryGetId(name: OverworldInput.CycleCommand, id: out var cycleId)) {
            return new Result(Message: "Overworld commands are not interned.", Passed: false);
        }

        var players = new Guid[OverworldWorld.MaxPlayers];

        for (var index = 0; (index < players.Length); index++) {
            players[index] = DeterministicGuid(salt: (uint)index);
        }

        var schedule = BuildSchedule(players: players);

        ISnapshotSource Script() => new ScriptedSnapshotSource(script: tick => BuildSnapshot(tick: tick, moveId: moveId, jumpId: jumpId, interactId: interactId, cycleId: cycleId));

        // First run records its input snapshots and the per-tick roster events it applied.
        var recorder = new InputRecorder(seed: seed);
        var capturedRoster = new List<ImmutableArray<RosterEvent>>();
        var first = Simulate(seed: seed, input: new RecordingSnapshotSource(inner: Script(), recorder: recorder), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, moveId: moveId, jumpId: jumpId, interactId: interactId, cycleId: cycleId, capturedRoster: capturedRoster);
        var second = Simulate(seed: seed, input: Script(), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, moveId: moveId, jumpId: jumpId, interactId: interactId, cycleId: cycleId, capturedRoster: null);
        var divergence = HashTrace.FirstDivergence(left: first, right: second);

        if (divergence >= 0) {
            return new Result(Message: $"NON-DETERMINISTIC across join/leave: two identical scripted runs diverged at tick {divergence}.", Passed: false);
        }

        // Replay fidelity, including a binary round-trip of the recording (input snapshots + roster events).
        var recording = new OverworldRecording { Input = recorder.ToRecording(), RosterEvents = [.. capturedRoster], };

        using var stream = new MemoryStream();

        recording.Write(stream: stream, registry: registry);
        stream.Position = 0L;

        var roundTripped = OverworldRecording.Read(stream: stream, registry: registry);
        var replayed = Simulate(seed: roundTripped.Input.Seed, input: new ReplaySnapshotSource(recording: roundTripped.Input), roster: new ReplayRosterEventSource(recording: roundTripped), ticks: ticks, moveId: moveId, jumpId: jumpId, interactId: interactId, cycleId: cycleId, capturedRoster: null);
        var replayDivergence = HashTrace.FirstDivergence(left: first, right: replayed);

        if (replayDivergence >= 0) {
            return new Result(Message: $"REPLAY DIVERGED from the recorded run at tick {replayDivergence}.", Passed: false);
        }

        // Planet-scale translation invariance: the same scripted run with the whole room in a far world cell must
        // produce an identical CELL-INVARIANT (local) trajectory.
        const long farCell = 1_000_000_000L;
        var nearLocal = Simulate(seed: seed, input: Script(), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, moveId: moveId, jumpId: jumpId, interactId: interactId, cycleId: cycleId, capturedRoster: null, localHash: true);
        var farLocal = Simulate(seed: seed, input: Script(), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, moveId: moveId, jumpId: jumpId, interactId: interactId, cycleId: cycleId, capturedRoster: null, spawnCell: farCell, localHash: true);
        var cellDivergence = HashTrace.FirstDivergence(left: nearLocal, right: farLocal);

        if (cellDivergence >= 0) {
            return new Result(Message: $"PLANET-SCALE INVARIANCE BROKEN: the cell 0 and cell {farCell:N0} local trajectories diverged at tick {cellDivergence}.", Passed: false);
        }

        return new Result(Message: $"determinism + replay + planet-scale cell-invariance (cell 0 ≡ cell {farCell:N0}) verified over {ticks} ticks with scheduled join/leave/recycle + cabinet insert/eject/cycle (peak {OverworldWorld.MaxPlayers} players; final hash 0x{first[^1]:X16})", Passed: true);
    }

    // Exercises empty→full, two joins on one tick (ordering), a mid-slot leave, and a recycling rejoin.
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

    // One tick's scripted input as command lanes — a lane per slot. Each slot circles its stick at its own phase, jumps
    // for 8 ticks every 90, and presses interact (insert/eject the nearest cabinet) at cycle 45 and cycle (advance the
    // nearest cabinet's cart) at cycle 70 — whichever cabinet the wander happened to reach, identical across runs.
    private static CommandSnapshot BuildSnapshot(ulong tick, ushort moveId, ushort jumpId, ushort interactId, ushort cycleId) {
        var lanes = ImmutableArray.CreateBuilder<CommandLane>(initialCapacity: OverworldWorld.MaxPlayers);

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            var entries = ImmutableArray.CreateBuilder<CommandEntry>(initialCapacity: 3);
            var angle = ((tick * 0.05f) + (slot * 1.7f));
            var cycle = (int)((tick + ((ulong)(slot * 13))) % 90u);

            entries.Add(item: new CommandEntry(CommandId: moveId, Value: CommandValue.Axis(value: new Vector2(x: MathF.Cos(x: angle), y: MathF.Sin(x: angle))), Phase: CommandPhase.Active));

            if (cycle == 0) {
                entries.Add(item: new CommandEntry(CommandId: jumpId, Value: CommandValue.Digital(active: true), Phase: CommandPhase.Started));
            } else if (cycle < 8) {
                entries.Add(item: new CommandEntry(CommandId: jumpId, Value: CommandValue.Digital(active: true), Phase: CommandPhase.Active));
            } else if (cycle == 8) {
                entries.Add(item: new CommandEntry(CommandId: jumpId, Value: CommandValue.Digital(active: false), Phase: CommandPhase.Completed));
            } else if (cycle == 45) {
                entries.Add(item: new CommandEntry(CommandId: interactId, Value: CommandValue.Digital(active: true), Phase: CommandPhase.Started));
            } else if (cycle == 70) {
                entries.Add(item: new CommandEntry(CommandId: cycleId, Value: CommandValue.Digital(active: true), Phase: CommandPhase.Started));
            }

            entries.Sort(comparison: static (left, right) => left.CommandId.CompareTo(value: right.CommandId));
            lanes.Add(item: new CommandLane(Entries: entries.DrainToImmutable(), Slot: slot));
        }

        return new CommandSnapshot(Lanes: lanes.DrainToImmutable(), Tick: tick);
    }

    private static ulong[] Simulate(uint seed, ISnapshotSource input, IRosterEventSource roster, int ticks, ushort moveId, ushort jumpId, ushort interactId, ushort cycleId, List<ImmutableArray<RosterEvent>>? capturedRoster, long spawnCell = 0L, bool localHash = false) {
        var world = new OverworldWorld(room: ScriptedRoom, tuning: PlatformerTuning.Default, tickSeconds: TickSeconds, seed: seed, spawnCellX: spawnCell, spawnCellZ: spawnCell, startLoaded: false);
        var hashes = new ulong[ticks];

        for (var index = 0; (index < ticks); index++) {
            var tick = world.CurrentTick;
            var applied = ImmutableArray.CreateBuilder<RosterEvent>();

            foreach (var rosterEvent in roster.EventsForTick(tick: tick)) {
                var slot = ((rosterEvent.Kind == RosterEventKind.Join)
                    ? world.AddPlayer(playerId: rosterEvent.PlayerId)
                    : world.RemovePlayer(playerId: rosterEvent.PlayerId));

                applied.Add(item: (rosterEvent with { Slot = slot }));
            }

            capturedRoster?.Add(item: applied.DrainToImmutable());

            var snapshot = input.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue);
            var intents = OverworldSnapshotProjection.ToIntents(snapshot: in snapshot, moveId: moveId, hasMove: true, jumpId: jumpId, hasJump: true, interactId: interactId, hasInteract: true, cycleId: cycleId, hasCycle: true);

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
