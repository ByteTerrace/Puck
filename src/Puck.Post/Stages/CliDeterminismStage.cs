using Puck.Commands;
using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// Tier-A stage A4. Proves the neutral sim can be driven DETERMINISTICALLY from the command line: a scripted console
/// session — real text commands run through <see cref="CommandRegistry.Submit"/>, each a
/// <see cref="CommandRouting.Simulation"/> line injected into the per-tick <see cref="CommandSnapshot"/> rather than run
/// inline — drives the fixed-point sim through the same deterministic path physical input uses. It asserts two identical
/// sessions produce identical per-tick state hashes, a record → binary round-trip → replay reproduces them bit-for-bit,
/// and the session measurably drove the sim (its final state differs from an undriven run).
/// </summary>
internal sealed class CliDeterminismStage : IPostStage {
    private const int Ticks = 600;
    private const uint Seed = 0x00C0FFEEu;

    private static readonly (int X, int Y)[] Directions = [(1, 0), (0, 1), (-1, 0), (0, -1)];
    private static readonly FixedQ4816 TickSeconds = FixedQ4816.FromDouble(value: (1d / 240d));

    /// <inheritdoc/>
    public string Name => "cli-determinism";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        // A registry over the console vocabulary, used to write/read the recording's id↔name table. Built from the same
        // module as each run, so interned ids match (and the table remaps them regardless).
        var registry = new CommandRegistry(modules: [new NeutralConsoleModule()]);

        if (!registry.TryGetId(name: NeutralInput.MoveCommand, id: out var moveId) ||
            !registry.TryGetId(name: NeutralInput.JumpCommand, id: out var jumpId)) {
            return PostStageOutcome.Infra(detail: "the console commands were not interned");
        }

        var recorder = new InputRecorder(seed: Seed);
        var live = RunLive(script: ConsoleScript, recorder: recorder, moveId: moveId, jumpId: jumpId);
        var repeat = RunLive(script: ConsoleScript, recorder: null, moveId: moveId, jumpId: jumpId);
        var baseline = RunLive(script: static _ => [], recorder: null, moveId: moveId, jumpId: jumpId);
        var sameDivergence = HashTrace.FirstDivergence(left: live, right: repeat);

        if (sameDivergence >= 0) {
            return PostStageOutcome.Fail(detail: $"non-deterministic: two identical console sessions diverged at tick {sameDivergence}");
        }

        if (live[^1] == baseline[^1]) {
            return PostStageOutcome.Fail(detail: "ineffective: the console session produced the same final state as an undriven run — the injected commands never reached the sim");
        }

        var recording = recorder.ToRecording();

        using var stream = new MemoryStream();

        SnapshotRecording.Write(stream: stream, recording: recording, registry: registry);
        stream.Position = 0L;

        var roundTripped = SnapshotRecording.Read(stream: stream, registry: registry);
        var replayed = Replay(recording: roundTripped, moveId: moveId, jumpId: jumpId);
        var replayDivergence = HashTrace.FirstDivergence(left: live, right: replayed);

        if (replayDivergence >= 0) {
            return PostStageOutcome.Fail(detail: $"replay diverged from the recorded console session after a binary round-trip at tick {replayDivergence}");
        }

        return PostStageOutcome.Pass(detail: $"deterministic CLI sim-control verified over {Ticks} ticks: a scripted console session is deterministic, replays bit-for-bit, and measurably drove the sim (final hash 0x{live[^1]:X16})");
    }

    // A believable console session: a move pulse every 4 ticks rotating through the cardinal directions, and a jump
    // impulse every 37 ticks. Every line is a real text command parsed by Submit.
    private static IEnumerable<string> ConsoleScript(int tick) {
        if ((tick % 4) == 0) {
            var (x, y) = Directions[(tick / 4) % Directions.Length];

            yield return $"move --x {x} --y {y}";
        }

        if ((tick % 37) == 0) {
            yield return "jump";
        }
    }

    // Drives the sim for `Ticks` ticks by submitting each tick's scripted console lines through the REAL Submit → inject
    // → router → snapshot path, optionally recording the produced snapshots. A manual capture clock stamps each
    // injection at its tick so the run is deterministic without a wall clock.
    private static ulong[] RunLive(Func<int, IEnumerable<string>> script, InputRecorder? recorder, ushort moveId, ushort jumpId) {
        var registry = new CommandRegistry(modules: [new NeutralConsoleModule()]);
        var clock = new ManualInputClock();
        var router = new InputRouter(bindings: NeutralInput.ConsoleBindings, clock: clock, registry: registry);

        // The front-door wiring under test: a Simulation-class submitted command is injected, not run inline.
        registry.RouteSimulationTo(sink: router);

        ISnapshotSource source = ((recorder is null)
            ? router
            : new RecordingSnapshotSource(inner: router, recorder: recorder));

        var sim = new NeutralSim(seed: Seed, tickSeconds: TickSeconds);
        var hashes = new ulong[Ticks];

        for (var tick = 0; (tick < Ticks); tick++) {
            var window = ((ulong)tick);

            // Stamp this tick, then submit its console lines: each Simulation command injects at capture tick == window.
            clock.NowTicks = window;

            foreach (var line in script(arg: tick)) {
                var result = registry.Submit(line: line);

                if (!result.Output.StartsWith(value: "[queued:", comparisonType: StringComparison.Ordinal)) {
                    throw new InvalidOperationException(message: $"console line did not queue as a simulation command: '{line}' -> '{result.Output}'");
                }
            }

            var snapshot = source.SnapshotForTick(tick: window, windowEndTick: (window + 1UL));
            var intent = NeutralInput.Project(snapshot: in snapshot, moveId: moveId, jumpId: jumpId);

            sim.Advance(intent: in intent);

            hashes[tick] = sim.StateHash();
        }

        return hashes;
    }

    // Replays the recorded snapshots verbatim (no console, no router) and re-hashes — the bit-for-bit reproduction.
    private static ulong[] Replay(SnapshotRecording recording, ushort moveId, ushort jumpId) {
        var source = new ReplaySnapshotSource(recording: recording);
        var sim = new NeutralSim(seed: recording.Seed, tickSeconds: TickSeconds);
        var hashes = new ulong[Ticks];

        for (var tick = 0; (tick < Ticks); tick++) {
            var snapshot = source.SnapshotForTick(tick: ((ulong)tick), windowEndTick: ulong.MaxValue);
            var intent = NeutralInput.Project(snapshot: in snapshot, moveId: moveId, jumpId: jumpId);

            sim.Advance(intent: in intent);

            hashes[tick] = sim.StateHash();
        }

        return hashes;
    }

    /// <summary>A capture clock whose tick is set explicitly per step, so injected commands stamp deterministically.</summary>
    private sealed class ManualInputClock : IInputClock {
        /// <inheritdoc/>
        public ulong NowTicks { get; set; }
    }
}
