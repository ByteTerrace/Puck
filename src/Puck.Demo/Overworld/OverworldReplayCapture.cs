using System.Collections.Immutable;
using System.Numerics;
using Puck.Commands;

namespace Puck.Demo.Overworld;

/// <summary>
/// A Puck.Replay seed: a fresh, throwaway <see cref="OverworldWorld"/> driven by a deterministic
/// scripted walk and captured through the SAME record/replay seams <see cref="OverworldDeterminism"/>'s harness
/// already proved (<see cref="RecordingSnapshotSource"/>, <see cref="ReplaySnapshotSource"/>,
/// <see cref="ReplayRosterEventSource"/>) — <c>replay.capture</c>'s implementation.
/// </summary>
/// <remarks>
/// SCOPING NOTE (read before extending): this captures a SCRIPTED window, not the live interactive session. The
/// live default demo run is CONSOLE MODE (<c>OverworldRenderNode.AdvanceConsoleMode</c>), which builds each tick's
/// <c>PlayerIntent[]</c> directly from <c>GamingBrickPadService</c>/<c>PagedInputBindings</c> — it never constructs
/// a <see cref="CommandSnapshot"/> at all, so there is no live snapshot stream to decorate with
/// <see cref="RecordingSnapshotSource"/> the way this file does. <see cref="RouterIntentSource"/> (which DOES own a
/// <see cref="Puck.Commands.ISnapshotSource"/>) is constructed ONLY on the bare-room path
/// (<c>OverworldRenderNode</c>'s <c>else if (GamepadManager manager)</c> branch — no cabinets); it is never live in
/// the default museum/console demo. Live-session recording would require
/// EITHER (a) a new per-tick recorder tapping <c>AdvanceConsoleMode</c>'s <c>firstTickIntents</c>/<c>heldIntents</c>
/// arrays directly (a <c>PlayerIntent[]</c>-shaped format, not <see cref="SnapshotRecording"/> — a materially
/// different, more invasive change to a file already at its CA1506 analyzer ceiling), or (b) a
/// <c>Puck.Replay</c>-owned world-state snapshot format so a mid-session recording can rehydrate the EXACT starting
/// state (roster, cabinet boot state, garden growth, …) a fresh <see cref="OverworldWorld"/> constructor cannot
/// reproduce on its own. Neither exists yet; this is the honest minimal version the task's own scoping note
/// invited: a persisted file <c>replay.verify</c> re-runs bit-for-bit is the non-negotiable core, and this delivers
/// exactly that over the REAL binary formats end to end.
/// </remarks>
public static class OverworldReplayCapture {
    /// <summary>~3 seconds at the capture's own 240 Hz tick rate — "a few seconds of scripted walking".</summary>
    public const int DefaultTicks = 720;
    /// <summary>The capture/verify tick period — matches <see cref="OverworldDeterminism"/>'s own constant (any
    /// fixed value proves determinism; reusing it keeps one canonical rate for every scripted overworld proof).</summary>
    private const float TickSeconds = (1f / 240f);
    // Mirrors OverworldDeterminism.ScriptedRoom byte-for-byte — the same reusable "4 cabinets + a shelf" shape every
    // scripted overworld proof in this codebase already builds against, so a captured tape's collision surface is a
    // known, precedented quantity rather than a fresh one-off.
    private static readonly OverworldRoom ScriptedRoom = OverworldRoom.WithConsolesAndShelf(consoleCount: 4, shelfCount: 1);

    /// <summary>The outcome of a <see cref="Capture"/> call.</summary>
    /// <param name="Recording">The captured recording, ready to persist.</param>
    /// <param name="FinalHash">The scripted run's final tick state hash.</param>
    /// <param name="TickCount">How many ticks were captured.</param>
    public readonly record struct CaptureResult(OverworldRecording Recording, ulong FinalHash, int TickCount);

    /// <summary>The outcome of a <see cref="Verify"/> call.</summary>
    /// <param name="FinalHash">The replayed run's final tick state hash.</param>
    /// <param name="TickCount">How many ticks were replayed (the recording's own snapshot count).</param>
    public readonly record struct VerifyResult(ulong FinalHash, int TickCount);

    /// <summary>Captures <paramref name="ticks"/> ticks of a deterministic scripted walk (one player, spawned at
    /// tick 0, wandering in a widening circle — Move axis only, so the tape reads as a clean continuous walk) through
    /// the real <see cref="RecordingSnapshotSource"/> decorator, producing a genuine <see cref="OverworldRecording"/>.</summary>
    /// <param name="seed">The simulation seed (also seeds the scripted player's identity, so two captures with
    /// different seeds never collide on the same synthetic player).</param>
    /// <param name="ticks">How many ticks to walk (clamped to at least 1 by the caller).</param>
    /// <returns>The captured recording plus its final hash/tick count.</returns>
    public static CaptureResult Capture(uint seed, int ticks) {
        var registry = BuildRegistry(moveId: out var moveId, jumpId: out var jumpId, interactId: out var interactId, cycleId: out var cycleId);
        var player = ScriptedPlayerId(seed: seed);
        var recorder = new InputRecorder(seed: seed);
        ISnapshotSource scripted = new ScriptedSnapshotSource(script: tick => BuildWalkSnapshot(tick: tick, moveId: moveId));
        var recordingSource = new RecordingSnapshotSource(inner: scripted, recorder: recorder);
        var roster = new ScriptedRosterEventSource(schedule: [(0UL, new RosterEvent(Kind: RosterEventKind.Join, PlayerId: player, Slot: 0))]);
        var capturedRoster = new List<ImmutableArray<RosterEvent>>(capacity: ticks);
        var finalHash = Simulate(seed: seed, input: recordingSource, roster: roster, ticks: ticks, moveId: moveId, jumpId: jumpId, interactId: interactId, cycleId: cycleId, capturedRoster: capturedRoster);
        var recording = new OverworldRecording { Input = recorder.ToRecording(), RosterEvents = [.. capturedRoster], };

        _ = registry; // only its interned ids are needed here — Write/Read rebuild an equivalent registry themselves.

        return new CaptureResult(FinalHash: finalHash, Recording: recording, TickCount: ticks);
    }

    /// <summary>Replays a recording through a FRESH <see cref="OverworldWorld"/> (same construction as
    /// <see cref="Capture"/>: <see cref="ScriptedRoom"/>, <see cref="PlatformerTuning.Default"/>,
    /// <see cref="TickSeconds"/>, the recording's own seed) — "a saved moment can re-happen" over the pipe. Calling
    /// this twice on the same recording returns the identical hash both times (pure function of the recorded input).</summary>
    /// <param name="recording">The recording to replay.</param>
    /// <returns>The replayed run's final hash and tick count.</returns>
    public static VerifyResult Verify(OverworldRecording recording) {
        ArgumentNullException.ThrowIfNull(argument: recording);

        BuildRegistry(moveId: out var moveId, jumpId: out var jumpId, interactId: out var interactId, cycleId: out var cycleId);

        var input = new ReplaySnapshotSource(recording: recording.Input);
        var roster = new ReplayRosterEventSource(recording: recording);
        var ticks = recording.Input.Snapshots.Length;
        var finalHash = Simulate(seed: recording.Input.Seed, input: input, roster: roster, ticks: ticks, moveId: moveId, jumpId: jumpId, interactId: interactId, cycleId: cycleId, capturedRoster: null);

        return new VerifyResult(FinalHash: finalHash, TickCount: ticks);
    }

    /// <summary>Builds the same command registry every capture/verify call needs, interning the overworld's four
    /// commands — <see cref="SnapshotRecording"/>'s id↔name table remap means a fresh registry built this way always
    /// resolves the ids referenced by a recording's command names.</summary>
    public static CommandRegistry BuildRegistry(out ushort moveId, out ushort jumpId, out ushort interactId, out ushort cycleId) {
        var registry = new CommandRegistry(modules: [new OverworldCommandModule()]);

        _ = registry.TryGetId(name: OverworldInput.MoveCommand, id: out moveId);
        _ = registry.TryGetId(name: OverworldInput.JumpCommand, id: out jumpId);
        _ = registry.TryGetId(name: OverworldInput.InteractCommand, id: out interactId);
        _ = registry.TryGetId(name: OverworldInput.CycleCommand, id: out cycleId);

        return registry;
    }

    // One player's per-tick circular wander — the SAME shape OverworldDeterminism.BuildSnapshot uses for its own
    // slot 0, without the jump/interact/cycle beats (a clean walking loop is the point of this tape, not a cabinet
    // interaction proof — OverworldDeterminism already owns that).
    private static CommandSnapshot BuildWalkSnapshot(ulong tick, ushort moveId) {
        var angle = (tick * 0.035f);
        var move = new Vector2(x: MathF.Cos(x: angle), y: MathF.Sin(x: angle));
        var entry = new CommandEntry(CommandId: moveId, Value: CommandValue.Axis(value: move), Phase: CommandPhase.Active);
        var lane = new CommandLane(Entries: [entry], Slot: 0);

        return new CommandSnapshot(Lanes: [lane], Tick: tick);
    }

    // Mirrors OverworldDeterminism.Simulate: a fresh world, roster events before that tick's intents, then Advance.
    private static ulong Simulate(uint seed, ISnapshotSource input, IRosterEventSource roster, int ticks, ushort moveId, ushort jumpId, ushort interactId, ushort cycleId, List<ImmutableArray<RosterEvent>>? capturedRoster) {
        var world = new OverworldWorld(room: ScriptedRoom, tuning: PlatformerTuning.Default, tickSeconds: TickSeconds, seed: seed, spawnCellX: 0L, spawnCellZ: 0L, startLoaded: false);
        var hash = 0UL;

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
            hash = world.StateHash();
        }

        return hash;
    }

    // A deterministic scripted-player guid, salted by the capture seed so distinct captures never collide.
    private static Guid ScriptedPlayerId(uint seed) {
        var bytes = new byte[16];

        _ = BitConverter.TryWriteBytes(destination: bytes, value: 0x8EA7_0000u | (seed & 0xFFFFu));

        return new Guid(b: bytes);
    }
}
