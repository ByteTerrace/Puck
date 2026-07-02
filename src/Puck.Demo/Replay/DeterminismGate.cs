using System.Collections.Immutable;
using System.Numerics;
using Puck.Commands;
using Puck.Demo.MiniAction;

namespace Puck.Demo.Replay;

/// <summary>
/// The engine-level determinism + replay self-check (pure CPU). It proves, by construction, the two headline
/// payoffs of the input/command core:
/// <list type="number">
/// <item><description>the fixed-point simulation type is correct (<see cref="FixedPointSelfTest"/>);</description></item>
/// <item><description>a per-tick <see cref="CommandSnapshot"/> stream drives the fixed-point sim to a bit-identical
/// per-tick state hash on every run (determinism), AND a record → binary round-trip → replay reproduces those hashes
/// exactly (replay fidelity through the neutral <see cref="SnapshotRecording"/> format);</description></item>
/// <item><description>every <see cref="CommandValueKind"/> — including a fused <see cref="CommandValueKind.Orientation"/>
/// quaternion — survives the binary round-trip bit-for-bit.</description></item>
/// </list>
/// It is the generalization of <c>--validate-mini-action</c>: the same proof, but through the engine's command
/// snapshot seam (the unit a peer transmits and a recorder stores) rather than MiniAction's bespoke intent log.
/// </summary>
public static class DeterminismGate {
    private const float TickSeconds = (1f / 240f); // the host's fixed step; any constant proves determinism
    private const uint Seed = 0x00C0FFEEu;

    private static readonly Guid Player = new(b: [0x01, 0x00, 0x00, 0xA5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,]);

    /// <summary>The outcome of a self-check.</summary>
    public readonly record struct Result(bool Passed, string Message);

    /// <summary>Runs the self-check over <paramref name="ticks"/> ticks of scripted command snapshots.</summary>
    /// <param name="ticks">The number of ticks to simulate.</param>
    /// <returns>The outcome.</returns>
    public static Result Run(int ticks = 600) {
        if (!FixedPointSelfTest.Run(out var fixedPointDetail)) {
            return new Result(Message: $"FIXED-POINT incorrect: {fixedPointDetail}", Passed: false);
        }

        if (!WorldCoord3SelfTest.Run(out var worldCoordDetail)) {
            return new Result(Message: $"WORLDCOORD3 incorrect: {worldCoordDetail}", Passed: false);
        }

        var registry = new CommandRegistry(modules: [new MiniActionCommandModule()]);

        if (!registry.TryGetId(name: MiniActionInput.MoveCommand, id: out var moveId) ||
            !registry.TryGetId(name: MiniActionInput.JumpCommand, id: out var jumpId)) {
            return new Result(Message: "MiniAction commands are not interned.", Passed: false);
        }

        // A scripted per-tick snapshot stream (a circling stick + a periodic jump for slot 0). Driving it through a
        // RecordingSnapshotSource captures exactly what the sim consumed.
        ISnapshotSource Script() => new ScriptedSnapshotSource(script: tick => BuildSnapshot(tick: tick, moveId: moveId, jumpId: jumpId));

        var recorder = new InputRecorder(seed: Seed);
        var live = Simulate(source: new RecordingSnapshotSource(inner: Script(), recorder: recorder), ticks: ticks, moveId: moveId, jumpId: jumpId);
        var repeat = Simulate(source: Script(), ticks: ticks, moveId: moveId, jumpId: jumpId);
        var sameDivergence = HashTrace.FirstDivergence(left: live, right: repeat);

        if (sameDivergence >= 0) {
            return new Result(Message: $"NON-DETERMINISTIC: the same snapshot stream produced different state at tick {sameDivergence}.", Passed: false);
        }

        // Binary round-trip the captured recording, then replay it: the snapshot format must reproduce the run exactly.
        var recording = recorder.ToRecording();

        using var stream = new MemoryStream();

        SnapshotRecording.Write(stream: stream, recording: recording, registry: registry);
        stream.Position = 0L;

        var roundTripped = SnapshotRecording.Read(stream: stream, registry: registry);
        var replayed = Simulate(source: new ReplaySnapshotSource(recording: roundTripped), ticks: ticks, moveId: moveId, jumpId: jumpId);
        var replayDivergence = HashTrace.FirstDivergence(left: live, right: replayed);

        if (replayDivergence >= 0) {
            return new Result(Message: $"REPLAY DIVERGED from the recorded run after a binary round-trip at tick {replayDivergence}.", Passed: false);
        }

        if (!ValueKindsRoundTrip(registry: registry, commandId: moveId, out var valueDetail)) {
            return new Result(Message: $"VALUE round-trip failed: {valueDetail}", Passed: false);
        }

        return new Result(Message: $"determinism + snapshot record/replay verified over {ticks} ticks (final hash 0x{live[^1]:X16}); all command value kinds round-trip", Passed: true);
    }

    // One tick's scripted snapshot: slot 0 circles its stick and jumps for 8 ticks every 90, expressed as the engine's
    // command lanes (an Axis2D move + a digital jump with press/hold/release edges).
    private static CommandSnapshot BuildSnapshot(ulong tick, ushort moveId, ushort jumpId) {
        var angle = (tick * 0.05d);
        var stick = new Vector2(x: ((float)Math.Cos(d: angle)), y: ((float)Math.Sin(a: angle)));
        var cycle = (int)(tick % 90u);
        var entries = ImmutableArray.CreateBuilder<CommandEntry>(initialCapacity: 2);

        entries.Add(item: new CommandEntry(CommandId: moveId, Value: CommandValue.Axis(value: stick), Phase: CommandPhase.Active));

        if (cycle == 0) {
            entries.Add(item: new CommandEntry(CommandId: jumpId, Value: CommandValue.Digital(active: true), Phase: CommandPhase.Started));
        } else if (cycle < 8) {
            entries.Add(item: new CommandEntry(CommandId: jumpId, Value: CommandValue.Digital(active: true), Phase: CommandPhase.Active));
        } else if (cycle == 8) {
            entries.Add(item: new CommandEntry(CommandId: jumpId, Value: CommandValue.Digital(active: false), Phase: CommandPhase.Completed));
        }

        entries.Sort(comparison: static (left, right) => left.CommandId.CompareTo(value: right.CommandId));

        return new CommandSnapshot(Lanes: [new CommandLane(Entries: entries.DrainToImmutable(), Slot: 0)], Tick: tick);
    }

    private static ulong[] Simulate(ISnapshotSource source, int ticks, ushort moveId, ushort jumpId) {
        var world = new MiniActionWorld(room: MiniActionRoom.Default, tuning: PlatformerTuning.Default, tickSeconds: TickSeconds, seed: Seed);

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

    // Round-trips one entry of each value kind through the binary format and asserts the value survives bit-for-bit —
    // proving the value-kind serialization (including the four-component Orientation quaternion).
    private static bool ValueKindsRoundTrip(CommandRegistry registry, ushort commandId, out string detail) {
        CommandValue[] values = [
            CommandValue.Digital(active: true),
            CommandValue.Axis(value: 0.5f),
            CommandValue.Axis(value: new Vector2(x: -0.25f, y: 0.75f)),
            CommandValue.Axis(value: new Vector3(x: 1f, y: -2f, z: 3f)),
            CommandValue.Orientation(value: Quaternion.Normalize(value: new Quaternion(x: 0.1f, y: 0.2f, z: 0.3f, w: 0.9f))),
        ];

        foreach (var value in values) {
            var entry = new CommandEntry(CommandId: commandId, Value: value, Phase: CommandPhase.Active);
            var recording = new SnapshotRecording {
                Seed = Seed,
                Snapshots = [new CommandSnapshot(Lanes: [new CommandLane(Entries: [entry], Slot: 0)], Tick: 7UL)],
            };

            using var stream = new MemoryStream();

            SnapshotRecording.Write(stream: stream, recording: recording, registry: registry);
            stream.Position = 0L;

            var back = SnapshotRecording.Read(stream: stream, registry: registry);

            if ((back.Snapshots.Length != 1) ||
                !back.Snapshots[0].TryGetLane(slot: 0, out var lane) ||
                !lane.TryGetEntry(commandId: commandId, entry: out var got)) {
                detail = $"{value.Kind} entry was lost";

                return false;
            }

            if (got.Value != value) {
                detail = $"{value.Kind} value changed: {got.Value} vs {value}";

                return false;
            }
        }

        detail = string.Empty;

        return true;
    }
}
