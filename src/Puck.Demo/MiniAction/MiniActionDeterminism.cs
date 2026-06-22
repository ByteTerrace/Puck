using System.Numerics;

namespace Puck.Demo.MiniAction;

/// <summary>
/// The day-one determinism + replay self-check, pure CPU (the simulation has no GPU dependency) — now exercised through
/// roster CHURN. It scripts a join/leave schedule (fill to capacity, two joins on one tick, a mid-slot leave, and a
/// recycling rejoin) plus scripted per-player input, runs it twice and asserts the two per-tick state-hash sequences are
/// identical (determinism), then records one run, round-trips it through the v2 binary format (intents + roster events),
/// replays it, and asserts the replayed hashes match (replay fidelity).
/// </summary>
public static class MiniActionDeterminism {
    private const float TickSeconds = (1f / 240f); // the host's fixed step; any constant proves determinism

    /// <summary>The outcome of a self-check.</summary>
    public readonly record struct Result(bool Passed, string Message);

    /// <summary>Runs the self-check over <paramref name="ticks"/> ticks with a scripted join/leave schedule.</summary>
    public static Result Run(int ticks = 1200) {
        const uint seed = 0x00C0FFEEu;

        var players = new Guid[MiniActionWorld.MaxPlayers];

        for (var index = 0; (index < players.Length); index++) {
            players[index] = DeterministicGuid(salt: (uint)index);
        }

        var schedule = BuildSchedule(players: players);

        // A deterministic per-(tick, slot) input: each slot circles the stick at its own phase and jumps periodically.
        static PlayerIntent Script(ulong tick, int slot) {
            var angle = ((tick * 0.05f) + (slot * 1.7f));
            var cycle = (int)((tick + (ulong)(slot * 13)) % 90u);

            return new PlayerIntent(
                Move: new Vector2(x: MathF.Cos(x: angle), y: MathF.Sin(x: angle)),
                JumpHeld: (cycle < 8),
                JumpPressed: (cycle == 0),
                JumpReleased: (cycle == 8)
            );
        }

        var first = Simulate(seed: seed, initialRoster: [], intents: new ScriptedIntentSource(script: Script), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, recorder: out var recorder);
        var second = Simulate(seed: seed, initialRoster: [], intents: new ScriptedIntentSource(script: Script), roster: new ScriptedRosterEventSource(schedule: schedule), ticks: ticks, recorder: out _);

        var divergence = FirstDivergence(a: first, b: second);

        if (divergence >= 0) {
            return new Result(Passed: false, Message: $"NON-DETERMINISTIC across join/leave: two identical scripted runs diverged at tick {divergence}.");
        }

        // Replay fidelity, including a v2 binary round-trip of the recording (intents + roster events).
        var replay = recorder.ToReplay();

        using var stream = new MemoryStream();

        replay.Write(stream: stream);
        stream.Position = 0;

        var roundTripped = MiniActionReplay.Read(stream: stream);
        var replayed = Simulate(seed: roundTripped.Seed, initialRoster: roundTripped.InitialRoster, intents: new ReplayIntentSource(replay: roundTripped), roster: new ReplayRosterEventSource(replay: roundTripped), ticks: ticks, recorder: out _);

        var replayDivergence = FirstDivergence(a: first, b: replayed);

        if (replayDivergence >= 0) {
            return new Result(Passed: false, Message: $"REPLAY DIVERGED from the recorded run at tick {replayDivergence}.");
        }

        return new Result(Passed: true, Message: $"determinism + replay verified over {ticks} ticks with scheduled join/leave/recycle (peak {MiniActionWorld.MaxPlayers} players; final hash 0x{first[^1]:X16})");
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
    private static ulong[] Simulate(uint seed, IReadOnlyList<Guid> initialRoster, IPlayerIntentSource intents, IRosterEventSource roster, int ticks, out ReplayRecorder recorder) {
        var world = new MiniActionWorld(room: MiniActionRoom.Default, tuning: PlatformerTuning.Default, tickSeconds: TickSeconds, seed: seed);

        foreach (var player in initialRoster) {
            _ = world.AddPlayer(playerId: player);
        }

        recorder = new ReplayRecorder(seed: seed, initialRoster: initialRoster);

        var hashes = new ulong[ticks];

        for (var index = 0; (index < ticks); index++) {
            var tick = world.CurrentTick;

            foreach (var rosterEvent in roster.EventsForTick(tick: tick)) {
                var slot = ((rosterEvent.Kind == RosterEventKind.Join)
                    ? world.AddPlayer(playerId: rosterEvent.PlayerId)
                    : world.RemovePlayer(playerId: rosterEvent.PlayerId));

                recorder.RecordRosterEvent(rosterEvent: (rosterEvent with { Slot = slot }));
            }

            intents.BeginFrame(firstTick: tick);

            var row = intents.CollectTick(tick: tick, players: world.RosterBySlot());

            recorder.Record(intentsBySlot: row);
            world.Advance(intentsBySlot: row);

            hashes[index] = world.StateHash();
        }

        return hashes;
    }
    private static int FirstDivergence(ulong[] a, ulong[] b) {
        var count = Math.Min(a.Length, b.Length);

        for (var index = 0; (index < count); index++) {
            if (a[index] != b[index]) {
                return index;
            }
        }

        return ((a.Length == b.Length) ? -1 : count);
    }
    private static Guid DeterministicGuid(uint salt) {
        var bytes = new byte[16];

        BitConverter.TryWriteBytes(destination: bytes, value: (0xA571_0000u | salt));

        return new Guid(b: bytes);
    }
}
