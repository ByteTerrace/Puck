using System.Collections.Immutable;
using System.Numerics;
using Puck.Commands;
using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// Tier-A stage A3. Proves the engine's command → snapshot → record → replay seam over the neutral fixed-point sim
/// (<see cref="NeutralSim"/>, standing in for the deferred game): a scripted per-tick <see cref="CommandSnapshot"/>
/// stream drives the sim to a bit-identical per-tick state hash on every run (determinism), a record → binary
/// round-trip → replay reproduces those hashes exactly (replay fidelity), and every <see cref="CommandValueKind"/>
/// — including a fused <see cref="CommandValueKind.Orientation"/> quaternion — survives the binary round-trip.
/// </summary>
internal sealed class DeterminismStage : IPostStage {
    private const int Ticks = 600;
    private const uint Seed = 0x00C0FFEEu;

    private static readonly FixedQ4816 TickSeconds = FixedQ4816.FromDouble(value: (1d / 240d));

    /// <inheritdoc/>
    public string Name => "determinism";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var registry = new CommandRegistry(modules: [new NeutralCommandModule()]);

        if (!registry.TryGetId(name: NeutralInput.MoveCommand, id: out var moveId) ||
            !registry.TryGetId(name: NeutralInput.JumpCommand, id: out var jumpId)) {
            return PostStageOutcome.Infra(detail: "the neutral commands were not interned");
        }

        // A scripted per-tick snapshot stream (a circling stick + a periodic jump for slot 0). A RecordingSnapshotSource
        // captures exactly what the sim consumed.
        ISnapshotSource Script() => new ScriptedSnapshotSource(script: tick => BuildSnapshot(tick: tick, moveId: moveId, jumpId: jumpId));

        var recorder = new InputRecorder(seed: Seed);
        var live = Simulate(source: new RecordingSnapshotSource(inner: Script(), recorder: recorder), moveId: moveId, jumpId: jumpId);
        var repeat = Simulate(source: Script(), moveId: moveId, jumpId: jumpId);
        var sameDivergence = HashTrace.FirstDivergence(left: live, right: repeat);

        if (sameDivergence >= 0) {
            return PostStageOutcome.Fail(detail: $"non-deterministic: the same snapshot stream produced different state at tick {sameDivergence}");
        }

        // Binary round-trip the captured recording, then replay it: the snapshot format must reproduce the run exactly.
        var recording = recorder.ToRecording();

        using var stream = new MemoryStream();

        SnapshotRecording.Write(stream: stream, recording: recording, registry: registry);
        stream.Position = 0L;

        var roundTripped = SnapshotRecording.Read(stream: stream, registry: registry);
        var replayed = Simulate(source: new ReplaySnapshotSource(recording: roundTripped), moveId: moveId, jumpId: jumpId);
        var replayDivergence = HashTrace.FirstDivergence(left: live, right: replayed);

        if (replayDivergence >= 0) {
            return PostStageOutcome.Fail(detail: $"replay diverged from the recorded run after a binary round-trip at tick {replayDivergence}");
        }

        if (!ValueKindsRoundTrip(registry: registry, commandId: moveId, detail: out var valueDetail)) {
            return PostStageOutcome.Fail(detail: $"value round-trip failed: {valueDetail}");
        }

        return PostStageOutcome.Pass(detail: $"determinism + snapshot record/replay verified over {Ticks} ticks (final hash 0x{live[^1]:X16}); all command value kinds round-trip");
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

    private static ulong[] Simulate(ISnapshotSource source, ushort moveId, ushort jumpId) {
        var sim = new NeutralSim(seed: Seed, tickSeconds: TickSeconds);
        var hashes = new ulong[Ticks];

        for (var tick = 0; (tick < Ticks); tick++) {
            var snapshot = source.SnapshotForTick(tick: ((ulong)tick), windowEndTick: ulong.MaxValue);
            var intent = NeutralInput.Project(snapshot: in snapshot, moveId: moveId, jumpId: jumpId);

            sim.Advance(intent: in intent);

            hashes[tick] = sim.StateHash();
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
            var recording = new SnapshotRecording {
                Seed = Seed,
                Snapshots = [new CommandSnapshot(Lanes: [new CommandLane(Entries: [new CommandEntry(CommandId: commandId, Value: value, Phase: CommandPhase.Active)], Slot: 0)], Tick: 7UL)],
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
                detail = $"{value.Kind} value changed";

                return false;
            }
        }

        detail = string.Empty;

        return true;
    }
}
