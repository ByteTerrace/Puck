using System.Numerics;
using System.Text;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One local seat active at record-start — the seat slice of the captured starting state, re-joined into the
/// replay's fresh world so its body exists to receive the recorded intent stream.</summary>
/// <param name="Slot">The 0-based seat slot.</param>
/// <param name="ProfileName">The profile the seat was seated on (re-resolved by name in the fresh world), or
/// <see langword="null"/> for a profileless seat.</param>
internal readonly record struct WorldReplaySeat(int Slot, string? ProfileName);

/// <summary>One recorded tick's server-facing input — the exact <see cref="IServerLink"/> traffic the live session
/// applied that tick, captured at the loopback: the synchronous authority <see cref="Commands"/> (applied before the
/// step, as the live command-apply window does) and the buffered per-entity <see cref="Intents"/> (drained at the
/// step). Re-applying these to a fresh world in the same order reproduces the tick.</summary>
/// <param name="Commands">The authority commands applied this tick (seat/console driving verbs), in submission order.</param>
/// <param name="Intents">The per-entity intent submissions buffered this tick (seat + addon driving), in submission order.</param>
internal readonly record struct WorldReplayTickInput(IReadOnlyList<WorldCommand> Commands, IReadOnlyList<IntentSubmission> Intents);

/// <summary>
/// A deterministic world-state recording: the SERVER simulation state captured at record-start plus the per-tick
/// server-input stream that drove the recorded span, so the recording rehydrates its own starting world and replays
/// through a fresh one. The starting state is the record-start <see cref="WorldDefinition"/> (embedded as its canonical
/// JSON) and the active seats — the population's body state at that instant is the deterministic boot image of that
/// definition (a fresh <see cref="WorldServer"/> reconstructs it exactly), so no per-body pose serialization is needed.
/// </summary>
/// <remarks>
/// <para>HONEST SCOPE. The captured state is the authoritative SERVER simulation only — the world definition, the active
/// seats, and the per-tick intent/command stream. Screen machines and their pixels, camera rigs, overlays, and audio are
/// PRESENTATION and are excluded: they are re-derived from the definition by the live client each frame and never feed
/// back into simulation, so a replay reproduces the authoritative population trajectory (the hashed poses) but does not
/// re-run the emulated cabinets or redraw the HUD.</para>
/// <para>DETERMINISM. Everything here is fixed-point or an exact integer tick — no wall-clock, no float in the hashed
/// state. A fresh world built from this recording and driven by the recorded stream produces a bit-identical per-tick
/// pose hash on every run, machine, and backend at a fixed code version. <see cref="Drive"/> is the one path both the
/// record side (to compute the recorded tail hash) and the replay side (to recompute it) run, so a match proves the
/// on-disk recording faithfully round-trips and the simulation is deterministic across fresh constructions.</para>
/// </remarks>
internal sealed class WorldReplaySnapshot {
    private const uint Magic = 0x504B_5250u; // "PKRP" — puck replay, distinct from SnapshotRecording's "PKRS".
    private const uint Version = 1u;

    /// <summary>The record-start world definition as its canonical UTF-8 JSON — the rehydrated starting state.</summary>
    public required byte[] DefinitionJson { get; init; }

    /// <summary>The seats active at record-start, re-joined into the fresh world before the stream replays.</summary>
    public required IReadOnlyList<WorldReplaySeat> Seats { get; init; }

    /// <summary>The per-tick server-input stream, in tick order from the recording's first tick.</summary>
    public required IReadOnlyList<WorldReplayTickInput> Ticks { get; init; }

    /// <summary>The tail state hash the record side computed by driving a fresh world through this exact stream — the
    /// value a replay recomputes and compares against.</summary>
    public required ulong RecordedTailHash { get; init; }

    /// <summary>The number of recorded ticks.</summary>
    public int TickCount => Ticks.Count;

    /// <summary>The deterministic per-tick state hash: every active body's fixed-point pose folded in index order, so two
    /// runs with identical input produce identical traces regardless of wall-clock or backend. The hashed scope is the
    /// authoritative population pose — the honest boundary of what a replay reproduces.</summary>
    /// <param name="population">The entity table to hash.</param>
    /// <returns>The state hash.</returns>
    public static ulong HashState(WorldPopulation population) {
        ArgumentNullException.ThrowIfNull(argument: population);

        var hash = Fnv1aHash.Create();

        for (var index = 0; (index < WorldPopulation.MaxPopulation); index++) {
            if (!population.IsActive(index: index) || (population.EntryBody(index: index) is not { } body)) {
                continue;
            }

            var position = body.FixedPosition;

            hash.Add(value: (uint)index);
            hash.Add(value: position.X.Value);
            hash.Add(value: position.Y.Value);
            hash.Add(value: position.Z.Value);
            hash.Add(value: body.FixedYaw.Value);
        }

        return hash.Value;
    }

    /// <summary>Rehydrates a FRESH authoritative world from this recording and re-drives the recorded server-input stream
    /// through it, returning the per-tick pose-hash trace. The one deterministic core both record and replay run: a fresh
    /// <see cref="WorldServer"/>/<see cref="WorldPopulation"/> is built from the embedded definition (its boot image is
    /// the starting body state), the recorded seats re-join, then each tick's commands apply (before the step, as the
    /// live command-apply window does) and its intents buffer and drain at the step — exactly the live per-tick order.</summary>
    /// <param name="profiles">The profile catalog seats re-resolve their name against (read-only here).</param>
    /// <returns>The per-tick state-hash trace, one entry per recorded tick.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/> is <see langword="null"/>.</exception>
    public ulong[] Drive(WorldProfiles profiles) {
        ArgumentNullException.ThrowIfNull(argument: profiles);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: DefinitionJson);
        var population = new WorldPopulation(definition: definition);
        // A fresh, unconfigured render envelope reads as "fits" — the replay applies no render-growing edits, and the
        // authoritative simulation never consults GPU capacity, so no probe is needed offline.
        var server = new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope());

        foreach (var seat in Seats) {
            _ = server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: seat.Slot), Slot: seat.Slot, ProfileName: seat.ProfileName, ProtocolVersion: WorldProtocol.Version));
        }

        var stepTicks = EngineTicks.PerRate(ratePerSecond: SimulationRate);
        var hashes = new ulong[Ticks.Count];

        for (var tick = 0; (tick < Ticks.Count); tick++) {
            var input = Ticks[tick];

            foreach (var command in input.Commands) {
                server.ApplyCommand(command: command);
            }

            foreach (var intent in input.Intents) {
                var submission = intent;

                server.EnqueueIntent(submission: in submission);
            }

            var context = new FixedStepContext(Tick: (ulong)tick, ElapsedTicks: ((ulong)(tick + 1) * stepTicks), StepTicks: stepTicks);

            server.Step(context: in context);
            hashes[tick] = HashState(population: population);
        }

        return hashes;
    }

    /// <summary>The fixed simulation rate (Hz) the recording assumes — the launcher's own <c>TargetUpdateRate</c>, a
    /// divisor of <see cref="EngineTicks.PerSecond"/>. Both the record and replay drives use it, so the step duration is
    /// identical on each side.</summary>
    public const uint SimulationRate = 240U;

    /// <summary>Serializes a recording to a stream in the <c>.puckreplay</c> binary form.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="recording">The recording to write.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static void Write(Stream stream, WorldReplaySnapshot recording) {
        ArgumentNullException.ThrowIfNull(argument: recording);
        ArgumentNullException.ThrowIfNull(argument: stream);

        using var writer = new BinaryWriter(output: stream, encoding: Encoding.UTF8, leaveOpen: true);

        writer.Write(value: Magic);
        writer.Write(value: Version);
        writer.Write(value: recording.RecordedTailHash);
        writer.Write(value: recording.DefinitionJson.Length);
        writer.Write(buffer: recording.DefinitionJson);

        writer.Write(value: recording.Seats.Count);

        foreach (var seat in recording.Seats) {
            writer.Write(value: seat.Slot);
            WriteNullableString(writer: writer, value: seat.ProfileName);
        }

        writer.Write(value: recording.Ticks.Count);

        foreach (var input in recording.Ticks) {
            writer.Write(value: input.Commands.Count);

            foreach (var command in input.Commands) {
                WriteCommand(writer: writer, command: command);
            }

            writer.Write(value: input.Intents.Count);

            foreach (var intent in input.Intents) {
                WriteIntent(writer: writer, submission: in intent);
            }
        }
    }

    /// <summary>Reads a recording from a stream.</summary>
    /// <param name="stream">The source stream.</param>
    /// <returns>The deserialized recording.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The stream is not a <c>.puckreplay</c> recording or is an unsupported version.</exception>
    public static WorldReplaySnapshot Read(Stream stream) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        using var reader = new BinaryReader(input: stream, encoding: Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadUInt32();
        var version = reader.ReadUInt32();

        if ((magic != Magic) || (version != Version)) {
            throw new InvalidDataException(message: "Not a .puckreplay recording, or an unsupported version.");
        }

        var recordedTailHash = reader.ReadUInt64();
        var definitionLength = reader.ReadInt32();
        var definitionJson = reader.ReadBytes(count: definitionLength);

        if (definitionJson.Length != definitionLength) {
            throw new InvalidDataException(message: "Truncated .puckreplay recording (definition).");
        }

        var seatCount = reader.ReadInt32();
        var seats = new List<WorldReplaySeat>(capacity: seatCount);

        for (var index = 0; (index < seatCount); index++) {
            var slot = reader.ReadInt32();
            var profileName = ReadNullableString(reader: reader);

            seats.Add(item: new WorldReplaySeat(Slot: slot, ProfileName: profileName));
        }

        var tickCount = reader.ReadInt32();
        var ticks = new List<WorldReplayTickInput>(capacity: tickCount);

        for (var index = 0; (index < tickCount); index++) {
            var commandCount = reader.ReadInt32();
            var commands = new List<WorldCommand>(capacity: commandCount);

            for (var command = 0; (command < commandCount); command++) {
                commands.Add(item: ReadCommand(reader: reader));
            }

            var intentCount = reader.ReadInt32();
            var intents = new List<IntentSubmission>(capacity: intentCount);

            for (var intent = 0; (intent < intentCount); intent++) {
                intents.Add(item: ReadIntent(reader: reader));
            }

            ticks.Add(item: new WorldReplayTickInput(Commands: commands, Intents: intents));
        }

        return new WorldReplaySnapshot {
            DefinitionJson = definitionJson,
            RecordedTailHash = recordedTailHash,
            Seats = seats,
            Ticks = ticks,
        };
    }

    private static void WriteIntent(BinaryWriter writer, in IntentSubmission submission) {
        writer.Write(value: submission.Tick);
        writer.Write(value: submission.EntityIndex);
        WriteIntentValue(writer: writer, intent: submission.Intent);
        WritePrincipal(writer: writer, principal: submission.Principal);
        writer.Write(value: (byte)submission.HeldLanes);
    }

    private static IntentSubmission ReadIntent(BinaryReader reader) {
        var tick = reader.ReadUInt64();
        var entityIndex = reader.ReadInt32();
        var intent = ReadIntentValue(reader: reader);
        var principal = ReadPrincipal(reader: reader);
        var heldLanes = (ActionLanes)reader.ReadByte();

        return new IntentSubmission(Tick: tick, EntityIndex: entityIndex, Intent: intent, Principal: principal, HeldLanes: heldLanes);
    }

    private static void WriteIntentValue(BinaryWriter writer, PlayerIntent intent) {
        writer.Write(value: intent.MoveForward.Value);
        writer.Write(value: intent.MoveStrafe.Value);
        writer.Write(value: intent.Turn.Value);
        writer.Write(value: intent.MoveUp.Value);
        writer.Write(value: intent.Pitch.Value);
        writer.Write(value: intent.Roll.Value);
        writer.Write(value: (byte)intent.Actions);
    }

    private static PlayerIntent ReadIntentValue(BinaryReader reader) {
        var moveForward = new FixedQ4816(Value: reader.ReadInt64());
        var moveStrafe = new FixedQ4816(Value: reader.ReadInt64());
        var turn = new FixedQ4816(Value: reader.ReadInt64());
        var moveUp = new FixedQ4816(Value: reader.ReadInt64());
        var pitch = new FixedQ4816(Value: reader.ReadInt64());
        var roll = new FixedQ4816(Value: reader.ReadInt64());
        var actions = (ActionLanes)reader.ReadByte();

        return new PlayerIntent(MoveForward: moveForward, MoveStrafe: moveStrafe, Turn: turn, MoveUp: moveUp, Pitch: pitch, Roll: roll, Actions: actions);
    }

    private static void WritePrincipal(BinaryWriter writer, WorldPrincipal principal) {
        writer.Write(value: (byte)principal.Kind);
        writer.Write(value: principal.Index);
        WriteNullableString(writer: writer, value: principal.Name);
    }

    private static WorldPrincipal ReadPrincipal(BinaryReader reader) {
        var kind = (PrincipalKind)reader.ReadByte();
        var index = reader.ReadInt32();
        var name = ReadNullableString(reader: reader);

        return new WorldPrincipal(Kind: kind, Index: index, Name: name);
    }

    // The authority-command tagged union — a closed set (WorldCommand's sealed subtypes), each written by a discriminant
    // byte plus its own fields over the shared Principal/EntityIndex base.
    private static void WriteCommand(BinaryWriter writer, WorldCommand command) {
        WritePrincipal(writer: writer, principal: command.Principal);
        writer.Write(value: command.EntityIndex);

        switch (command) {
            case WorldCommand.Teleport teleport:
                writer.Write(value: (byte)0);
                writer.Write(value: teleport.Position.X);
                writer.Write(value: teleport.Position.Y);
                writer.Write(value: teleport.Position.Z);
                writer.Write(value: teleport.YawRadians);
                writer.Write(value: teleport.PitchRadians);
                writer.Write(value: teleport.RollRadians);
                writer.Write(value: (byte)teleport.Kind);

                break;
            case WorldCommand.Face face:
                writer.Write(value: (byte)1);
                writer.Write(value: face.YawRadians);

                break;
            case WorldCommand.EnqueueSegment segment:
                writer.Write(value: (byte)2);
                WriteIntentValue(writer: writer, intent: segment.Intent);
                writer.Write(value: segment.Seconds);

                break;
            case WorldCommand.PressLane press:
                writer.Write(value: (byte)3);
                writer.Write(value: (byte)press.Lane);
                writer.Write(value: press.HoldSeconds.HasValue);
                writer.Write(value: (press.HoldSeconds ?? 0f));

                break;
            case WorldCommand.SetMotion motion:
                writer.Write(value: (byte)4);
                writer.Write(value: (byte)motion.Model);

                break;
            case WorldCommand.SetControl control:
                writer.Write(value: (byte)5);
                writer.Write(value: (byte)control.Source);

                break;
            case WorldCommand.Reconcile reconcile:
                writer.Write(value: (byte)6);
                writer.Write(value: reconcile.X);
                writer.Write(value: reconcile.Z);
                writer.Write(value: reconcile.YawRadians);
                writer.Write(value: reconcile.Seconds);

                break;
            case WorldCommand.Stop:
                writer.Write(value: (byte)7);

                break;
            default:
                throw new InvalidOperationException(message: $"no .puckreplay encoding for command kind '{command.GetType().Name}'.");
        }
    }

    private static WorldCommand ReadCommand(BinaryReader reader) {
        var principal = ReadPrincipal(reader: reader);
        var entityIndex = reader.ReadInt32();
        var kind = reader.ReadByte();

        return kind switch {
            0 => new WorldCommand.Teleport(Principal: principal, EntityIndex: entityIndex, Position: new Vector3(x: reader.ReadSingle(), y: reader.ReadSingle(), z: reader.ReadSingle()), YawRadians: reader.ReadSingle(), PitchRadians: reader.ReadSingle(), RollRadians: reader.ReadSingle(), Kind: (TeleportKind)reader.ReadByte()),
            1 => new WorldCommand.Face(Principal: principal, EntityIndex: entityIndex, YawRadians: reader.ReadSingle()),
            2 => new WorldCommand.EnqueueSegment(Principal: principal, EntityIndex: entityIndex, Intent: ReadIntentValue(reader: reader), Seconds: reader.ReadSingle()),
            3 => new WorldCommand.PressLane(Principal: principal, EntityIndex: entityIndex, Lane: (ActionLanes)reader.ReadByte(), HoldSeconds: ReadNullableSingle(reader: reader)),
            4 => new WorldCommand.SetMotion(Principal: principal, EntityIndex: entityIndex, Model: (MotionModel)reader.ReadByte()),
            5 => new WorldCommand.SetControl(Principal: principal, EntityIndex: entityIndex, Source: (IntentSource)reader.ReadByte()),
            6 => new WorldCommand.Reconcile(Principal: principal, EntityIndex: entityIndex, X: reader.ReadSingle(), Z: reader.ReadSingle(), YawRadians: reader.ReadSingle(), Seconds: reader.ReadSingle()),
            7 => new WorldCommand.Stop(Principal: principal, EntityIndex: entityIndex),
            _ => throw new InvalidDataException(message: $"unknown .puckreplay command discriminant {kind}."),
        };
    }

    // The PressLane hold is written as (bool present, float value) so the float slot is always consumed; the value is
    // meaningful only when the present flag is set, else the command carried no explicit hold.
    private static float? ReadNullableSingle(BinaryReader reader) {
        var present = reader.ReadBoolean();
        var value = reader.ReadSingle();

        return (present ? value : null);
    }

    private static void WriteNullableString(BinaryWriter writer, string? value) {
        writer.Write(value: (value is not null));

        if (value is not null) {
            writer.Write(value: value);
        }
    }

    private static string? ReadNullableString(BinaryReader reader) {
        return (reader.ReadBoolean() ? reader.ReadString() : null);
    }
}
