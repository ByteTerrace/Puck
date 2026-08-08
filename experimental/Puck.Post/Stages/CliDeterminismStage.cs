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
    private const uint Seed = 0x00C0FFEEu;
    private const int Ticks = 600;

    private static readonly (int X, int Y)[] Directions = [(1, 0), (0, 1), (-1, 0), (0, -1)];
    private static readonly FixedQ4816 TickSeconds = FixedQ4816.FromDouble(value: (1d / 240d));

    /// <inheritdoc/>
    public string Name => "cli-determinism";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (VerifySameTickFifo() is { } fifoFailure) {
            return PostStageOutcome.Fail(detail: fifoFailure);
        }

        // A registry over the console vocabulary, used to write/read the recording's id↔name table. Built from the same
        // module as each run, so interned ids match (and the table remaps them regardless).
        var registry = new CommandRegistry(modules: [new NeutralConsoleModule()]);

        if (!registry.TryGetId(name: NeutralInput.MoveCommand, id: out var moveId) ||
            !registry.TryGetId(name: NeutralInput.JumpCommand, id: out var jumpId)) {
            return PostStageOutcome.Infra(detail: "the console commands were not interned");
        }

        var baseline = RunLive(script: static _ => [], decorate: static source => source, moveId: moveId, jumpId: jumpId);

        var report = DeterminismHarness.Verify(
            seed: Seed,
            registry: registry,
            runScripted: decorate => RunLive(script: ConsoleScript, decorate: decorate, moveId: moveId, jumpId: jumpId),
            runReplay: recording => Replay(recording: recording, moveId: moveId, jumpId: jumpId)
        );

        if (report.Verdict == DeterminismVerdict.NonDeterministic) {
            return PostStageOutcome.Fail(detail: $"non-deterministic: two identical console sessions diverged at tick {report.DivergenceTick}");
        }

        if (report.LiveHashes[^1] == baseline[^1]) {
            return PostStageOutcome.Fail(detail: "ineffective: the console session produced the same final state as an undriven run — the injected commands never reached the sim");
        }

        if (report.Verdict == DeterminismVerdict.ReplayDiverged) {
            return PostStageOutcome.Fail(detail: $"replay diverged from the recorded console session after a binary round-trip at tick {report.DivergenceTick}");
        }

        return PostStageOutcome.Pass(detail: $"deterministic CLI sim-control verified over {Ticks} ticks: same-tick repeated verbs remain FIFO through record/replay, a scripted session is deterministic, and it measurably drove the sim (final hash 0x{report.LiveHashes[^1]:X16})");
    }

    private static string? VerifySameTickFifo() {
        var module = new FifoModule();
        var registry = new CommandRegistry(modules: [module]);
        var clock = new ManualInputClock { NowTicks = 1UL, };
        var router = new InputRouter(bindings: NeutralInput.ConsoleBindings, clock: clock, registry: registry);

        registry.RouteSimulationTo(sink: router);
        _ = registry.Submit(line: "fifo first");
        _ = registry.Submit(line: "fifo second");

        if (!registry.TryGetId(name: "fifo", id: out var fifoId)) {
            return "same-tick FIFO command was not interned";
        }

        router.Inject(injection: new CommandInjection(
            CaptureTick: 1UL,
            CommandId: fifoId,
            Phase: CommandPhase.Started,
            Slot: 3,
            Text: "fifo third",
            Value: CommandValue.Digital(active: true)
        ));

        var snapshot = router.SnapshotForTick(tick: 0UL, windowEndTick: 2UL);

        registry.ApplySnapshot(snapshot: in snapshot);

        if (!module.Seen.SequenceEqual(second: ["0:first", "0:second", "3:third"], comparer: StringComparer.Ordinal)) {
            return $"same-tick FIFO collapsed or reordered before recording: [{string.Join(separator: ",", values: module.Seen)}]";
        }

        using var stream = new MemoryStream();

        SnapshotRecording.Write(
            stream: stream,
            recording: new SnapshotRecording { Seed = Seed, Snapshots = [snapshot], },
            registry: registry
        );
        stream.Position = 0;

        var replayModule = new FifoModule();
        var replayRegistry = new CommandRegistry(modules: [replayModule]);
        var recording = SnapshotRecording.Read(stream: stream, registry: replayRegistry);
        var replay = recording.Snapshots[0];

        replayRegistry.ApplySnapshot(snapshot: in replay);

        return (replayModule.Seen.SequenceEqual(second: ["0:first", "0:second", "3:third"], comparer: StringComparer.Ordinal)
            ? null
            : $"same-tick FIFO collapsed or reordered after record/replay: [{string.Join(separator: ",", values: replayModule.Seen)}]");
    }

    // A believable console session: a move pulse every 4 ticks rotating through the cardinal directions, and a jump
    // impulse every 37 ticks. Every line is a real text command parsed by Submit.
    private static IEnumerable<string> ConsoleScript(int tick) {
        if ((tick % 4) == 0) {
            var (x, y) = Directions[((tick / 4) % Directions.Length)];

            yield return $"move --x {x} --y {y}";
        }

        if ((tick % 37) == 0) {
            yield return "jump";
        }
    }

    // Drives the sim for `Ticks` ticks by submitting each tick's scripted console lines through the REAL Submit → inject
    // → router → snapshot path, applying `decorate` to the router (identity, or a recording wrap). A manual capture
    // clock stamps each injection at its tick so the run is deterministic without a wall clock.
    private static ulong[] RunLive(Func<int, IEnumerable<string>> script, Func<ISnapshotSource, ISnapshotSource> decorate, ushort moveId, ushort jumpId) {
        var registry = new CommandRegistry(modules: [new NeutralConsoleModule()]);
        var clock = new ManualInputClock();
        var router = new InputRouter(bindings: NeutralInput.ConsoleBindings, clock: clock, registry: registry);

        // The front-door wiring under test: a Simulation-class submitted command is injected, not run inline.
        registry.RouteSimulationTo(sink: router);

        var source = decorate(router);

        var sim = new NeutralSim(seed: Seed, tickSeconds: TickSeconds);
        var hashes = new ulong[Ticks];

        for (var tick = 0; (tick < Ticks); tick++) {
            var window = ((ulong)tick);

            // Stamp this tick, then submit its console lines: each Simulation command injects at capture tick == window.
            clock.NowTicks = window;

            foreach (var line in script(arg: tick)) {
                var result = registry.Submit(line: line);

                if (!string.IsNullOrEmpty(value: result.Output)) {
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
    private sealed class FifoModule : ICommandModule {
        public List<string> Seen { get; } = [];

        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "fifo",
                description: "Same-tick FIFO probe.",
                handler: (context, args) => {
                    if (args.Count == 1) {
                        Seen.Add(item: $"{context.Slot}:{args[0].ToString()}");
                    }

                    return CommandResult.None;
                },
                routing: CommandRouting.Simulation
            );
        }
    }
}
