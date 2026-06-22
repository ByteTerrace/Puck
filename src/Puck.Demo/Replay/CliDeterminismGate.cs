using System.CommandLine;
using System.Numerics;
using Puck.Commands;
using Puck.Demo.MiniAction;

namespace Puck.Demo.Replay;

/// <summary>
/// The deterministic command-line / STDIN self-check (pure CPU). It proves the headline requirement that the
/// simulation can be driven <em>deterministically</em> from the command line: a scripted console session — real
/// command lines run through <see cref="CommandRegistry.Submit"/> — drives the fixed-point sim through the SAME
/// deterministic path physical input uses. Each <see cref="CommandRouting.Simulation"/> line is resolved and
/// <see cref="InputRouter.Inject">injected</see> into the per-tick <see cref="CommandSnapshot"/>, never run inline,
/// so it is tick-aligned, recorded, and replayed by the existing machinery. The gate asserts three things:
/// <list type="number">
/// <item><description>two identical scripted console sessions produce identical per-tick state hashes (determinism);</description></item>
/// <item><description>recording the session's snapshots, round-tripping them through the neutral binary format, and replaying
/// reproduces those hashes bit-for-bit (replay fidelity);</description></item>
/// <item><description>the console session measurably drove the sim — its final state differs from an undriven run (the injected
/// commands actually reached the simulation, rather than being silently dropped).</description></item>
/// </list>
/// Where <see cref="DeterminismGate"/> scripts the snapshot stream directly, this drives it from text through the real
/// console front door — the proof that a piped command stream is a first-class, deterministic sim-driving input.
/// </summary>
public static class CliDeterminismGate {
    private const float TickSeconds = (1f / 240f); // the host's fixed step; any constant proves determinism
    private const uint Seed = 0x00C0FFEEu;

    private static readonly Guid Player = new(b: [0x01, 0x00, 0x00, 0xA5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,]);

    // Integer-valued move directions so each scripted line ("move --x 1 --y 0") parses and round-trips bit-for-bit.
    private static readonly (int X, int Y)[] Directions = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>The outcome of a self-check.</summary>
    public readonly record struct Result(bool Passed, string Message);

    /// <summary>Runs the self-check over <paramref name="ticks"/> ticks of a scripted console command session.</summary>
    /// <param name="ticks">The number of ticks to simulate.</param>
    /// <returns>The outcome.</returns>
    public static Result Run(int ticks = 600) {
        // A registry over the console command vocabulary, used to write/read the recording's id<->name table. Built
        // from the same module as each live run, so interned ids match (and the table remaps them regardless).
        var registry = new CommandRegistry(modules: [new ConsoleSimModule()]);

        if (!registry.TryGetId(name: MiniActionInput.MoveCommand, id: out var moveId) ||
            !registry.TryGetId(name: MiniActionInput.JumpCommand, id: out var jumpId)) {
            return new Result(Message: "Console simulation commands are not interned.", Passed: false);
        }

        // The driven run records exactly the snapshots the console session produced; an identical second run proves
        // determinism; an unmapped (empty) run is the baseline that proves the console input had an effect.
        var recorder = new InputRecorder(seed: Seed);
        var live = RunLive(script: ConsoleScript, recorder: recorder, ticks: ticks, moveId: moveId, jumpId: jumpId);
        var repeat = RunLive(script: ConsoleScript, recorder: null, ticks: ticks, moveId: moveId, jumpId: jumpId);
        var baseline = RunLive(script: static _ => [], recorder: null, ticks: ticks, moveId: moveId, jumpId: jumpId);
        var sameDivergence = HashTrace.FirstDivergence(left: live, right: repeat);

        if (sameDivergence >= 0) {
            return new Result(Message: $"NON-DETERMINISTIC: two identical console sessions diverged at tick {sameDivergence}.", Passed: false);
        }

        if (live[^1] == baseline[^1]) {
            return new Result(Message: "INEFFECTIVE: the console session produced the same final state as an undriven run — the injected commands never reached the simulation.", Passed: false);
        }

        // Record -> binary round-trip -> replay: the captured console-driven snapshots must reproduce the run exactly.
        var recording = recorder.ToRecording();

        using var stream = new MemoryStream();

        SnapshotRecording.Write(stream: stream, recording: recording, registry: registry);
        stream.Position = 0L;

        var roundTripped = SnapshotRecording.Read(stream: stream, registry: registry);
        var replayed = Replay(recording: roundTripped, ticks: ticks, moveId: moveId, jumpId: jumpId);
        var replayDivergence = HashTrace.FirstDivergence(left: live, right: replayed);

        if (replayDivergence >= 0) {
            return new Result(Message: $"REPLAY DIVERGED from the recorded console session after a binary round-trip at tick {replayDivergence}.", Passed: false);
        }

        return new Result(Message: $"deterministic CLI sim-control verified over {ticks} ticks: a scripted console session (move/jump via Submit->inject->snapshot) is deterministic, replays bit-for-bit after a binary round-trip, and measurably drove the sim (final hash 0x{live[^1]:X16})", Passed: true);
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

    // Drives the sim for `ticks` ticks by submitting each tick's scripted console lines through the REAL
    // Submit -> inject -> router -> snapshot path, optionally recording the produced snapshots. A manual capture
    // clock stamps each injection at its tick so the run is deterministic without a wall clock.
    private static ulong[] RunLive(Func<int, IEnumerable<string>> script, InputRecorder? recorder, int ticks, ushort moveId, ushort jumpId) {
        var registry = new CommandRegistry(modules: [new ConsoleSimModule()]);
        var clock = new ManualInputClock();
        var router = new InputRouter(bindings: MiniActionInput.DefaultBindings, clock: clock, registry: registry);

        // The front-door wiring under test: a Simulation-class submitted command is injected, not run inline.
        registry.RouteSimulationTo(sink: router);

        ISnapshotSource source = ((recorder is null)
            ? router
            : new RecordingSnapshotSource(inner: router, recorder: recorder));

        var world = new MiniActionWorld(room: MiniActionRoom.Default, tuning: PlatformerTuning.Default, tickSeconds: TickSeconds, seed: Seed);

        _ = world.AddPlayer(playerId: Player);

        var hashes = new ulong[ticks];

        for (var tick = 0; (tick < ticks); tick++) {
            var window = ((ulong)tick);

            // Stamp this tick, then submit its console lines: each Simulation command injects at capture tick == window.
            clock.NowTicks = window;

            foreach (var line in script(arg: tick)) {
                // Every scripted line must resolve to an injection ("[queued: ...]"). Asserting it closes a silent
                // hole: a line the parser rejected (e.g. a mishandled negative axis) would otherwise be dropped, and
                // the gate would still pass on the surviving inputs while proving less than it claims.
                var result = registry.Submit(line: line);

                if (!result.Output.StartsWith(value: "[queued:", comparisonType: StringComparison.Ordinal)) {
                    throw new InvalidOperationException(message: $"Console line did not queue as a simulation command: '{line}' -> '{result.Output}'.");
                }
            }

            // Pull the tick's snapshot (folding the just-injected commands, whose capture tick precedes the window
            // close), project to intents, and step — the exact shape of the host loop.
            var snapshot = source.SnapshotForTick(tick: window, windowEndTick: (window + 1UL));
            var intents = MiniActionSnapshotProjection.ToIntents(snapshot: in snapshot, moveId: moveId, hasMove: true, jumpId: jumpId, hasJump: true);

            world.Advance(intentsBySlot: intents);

            hashes[tick] = world.StateHash();
        }

        return hashes;
    }

    // Replays the recorded snapshots verbatim (no console, no router) and re-hashes — the bit-for-bit reproduction.
    private static ulong[] Replay(SnapshotRecording recording, int ticks, ushort moveId, ushort jumpId) {
        var source = new ReplaySnapshotSource(recording: recording);
        var world = new MiniActionWorld(room: MiniActionRoom.Default, tuning: PlatformerTuning.Default, tickSeconds: TickSeconds, seed: recording.Seed);

        _ = world.AddPlayer(playerId: Player);

        var hashes = new ulong[ticks];

        for (var tick = 0; (tick < ticks); tick++) {
            var snapshot = source.SnapshotForTick(tick: ((ulong)tick), windowEndTick: ulong.MaxValue);
            var intents = MiniActionSnapshotProjection.ToIntents(snapshot: in snapshot, moveId: moveId, hasMove: true, jumpId: jumpId, hasJump: true);

            world.Advance(intentsBySlot: intents);

            hashes[tick] = world.StateHash();
        }

        return hashes;
    }

    /// <summary>A capture clock whose tick is set explicitly per step, so injected commands stamp deterministically.</summary>
    private sealed class ManualInputClock : IInputClock {
        /// <inheritdoc/>
        public ulong NowTicks { get; set; }
    }

    /// <summary>
    /// The console-facing command vocabulary the gate drives: <c>move</c> (an Axis2D with <c>--x</c>/<c>--y</c>) and
    /// <c>jump</c> (a digital impulse), both <see cref="CommandRouting.Simulation"/> and named to MiniAction's
    /// interned ids so their snapshot lanes project through the real <see cref="MiniActionSnapshotProjection"/>. The
    /// handlers are no-ops — the sim reads the values from the per-tick snapshot, not via dispatch.
    /// </summary>
    private sealed class ConsoleSimModule : ICommandModule {
        /// <inheritdoc/>
        public IEnumerable<CommandDefinition> GetCommands() {
            var xOption = new Option<float>(name: "--x") { Description = "The horizontal move axis, -1 to 1.", };
            var yOption = new Option<float>(name: "--y") { Description = "The vertical move axis, -1 to 1.", };

            yield return new CommandDefinition(
                Description: "Camera-relative move for the local player (console).",
                Handler: static _ => CommandResult.None,
                Name: MiniActionInput.MoveCommand,
                TextCommand: new Command(name: MiniActionInput.MoveCommand, description: "Move the local player.") {
                    xOption,
                    yOption,
                },
                ValueKind: CommandValueKind.Axis2D,
                ValueSelector: parse => CommandValue.Axis(value: new Vector2(x: parse.GetValue(option: xOption), y: parse.GetValue(option: yOption)))
            ) {
                Aliases = ["move"],
                Routing = CommandRouting.Simulation,
            };
            yield return CommandDefinition.Verb(
                aliases: ["jump"],
                description: "Jump for the local player (console).",
                handler: static _ => CommandResult.None,
                name: MiniActionInput.JumpCommand,
                routing: CommandRouting.Simulation,
                valueKind: CommandValueKind.Digital
            );
        }
    }
}
