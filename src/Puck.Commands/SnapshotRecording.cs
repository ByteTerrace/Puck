using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace Puck.Commands;

/// <summary>
/// A complete, replayable recording of a session's input: the simulation seed plus the per-tick
/// <see cref="CommandSnapshot"/> stream the host produced. Because a snapshot is a pure, deterministic function of
/// the captured input, replaying these reproduces the session bit-for-bit. The binary form embeds the command
/// id↔name table, so a recording survives a rebuild that reassigns interned ids (each entry is remapped by name on
/// load); lanes are serialized by value-kind (only the components a kind uses); and <see cref="CommandEntry.Device"/>
/// — a local-only annotation — is deliberately excluded, since it is never part of the cross-machine identity.
/// </summary>
/// <remarks>This is the seed of a future <c>Puck.Replay</c> project.</remarks>
public sealed class SnapshotRecording {
    private const uint Magic = 0x504B_5253u; // "PKRS"
    private const uint Version = 1u;

    /// <summary>The simulation seed the recorded run was created with.</summary>
    public required uint Seed { get; init; }
    /// <summary>The per-tick command snapshots, in tick order from the run's first tick.</summary>
    public required ImmutableArray<CommandSnapshot> Snapshots { get; init; }

    /// <summary>Serializes a recording to a stream, embedding <paramref name="registry"/>'s id↔name table for rebuild-safe replay.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="recording">The recording to write.</param>
    /// <param name="registry">The registry whose interned ids the recording's entries reference.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static void Write(Stream stream, SnapshotRecording recording, CommandRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: recording);
        ArgumentNullException.ThrowIfNull(argument: registry);
        ArgumentNullException.ThrowIfNull(argument: stream);

        using var writer = new BinaryWriter(output: stream, encoding: Encoding.UTF8, leaveOpen: true);

        writer.Write(value: Magic);
        writer.Write(value: Version);
        writer.Write(value: recording.Seed);

        // The command id↔name table: name by interned id, so a reader on a rebuilt registry can remap each entry.
        writer.Write(value: registry.CommandCount);

        for (var id = 0; (id < registry.CommandCount); id++) {
            writer.Write(value: registry.GetName(id: ((ushort)id)));
        }

        writer.Write(value: recording.Snapshots.Length);

        foreach (var snapshot in recording.Snapshots) {
            writer.Write(value: snapshot.Tick);

            var lanes = snapshot.Lanes;

            writer.Write(value: (lanes.IsDefaultOrEmpty ? 0 : lanes.Length));

            if (lanes.IsDefaultOrEmpty) {
                continue;
            }

            foreach (var lane in lanes) {
                var entries = lane.Entries;

                writer.Write(value: lane.Slot);
                writer.Write(value: (entries.IsDefaultOrEmpty ? 0 : entries.Length));

                if (entries.IsDefaultOrEmpty) {
                    continue;
                }

                foreach (var entry in entries) {
                    writer.Write(value: entry.CommandId);
                    writer.Write(value: ((byte)entry.Phase));
                    WriteValue(writer: writer, value: entry.Value);
                }
            }
        }
    }

    /// <summary>Reads a recording from a stream, remapping each entry's command id from name into <paramref name="registry"/>.</summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="registry">The current registry the replayed entries are remapped into.</param>
    /// <returns>The deserialized recording, with entries reindexed to the current registry (any entry naming an unknown command is dropped).</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The stream is not a snapshot recording or is an unsupported version.</exception>
    public static SnapshotRecording Read(Stream stream, CommandRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        ArgumentNullException.ThrowIfNull(argument: stream);

        using var reader = new BinaryReader(input: stream, encoding: Encoding.UTF8, leaveOpen: true);

        if ((reader.ReadUInt32() != Magic) || (reader.ReadUInt32() != Version)) {
            throw new InvalidDataException(message: "Not a snapshot recording, or an unsupported version.");
        }

        var seed = reader.ReadUInt32();
        var nameCount = reader.ReadInt32();
        var names = new string[nameCount];

        for (var index = 0; (index < nameCount); index++) {
            names[index] = reader.ReadString();
        }

        var snapshotCount = reader.ReadInt32();
        var snapshots = ImmutableArray.CreateBuilder<CommandSnapshot>(initialCapacity: snapshotCount);

        for (var snapshotIndex = 0; (snapshotIndex < snapshotCount); snapshotIndex++) {
            var tick = reader.ReadUInt64();
            var laneCount = reader.ReadInt32();
            var lanes = ImmutableArray.CreateBuilder<CommandLane>(initialCapacity: laneCount);

            for (var laneIndex = 0; (laneIndex < laneCount); laneIndex++) {
                var slot = reader.ReadInt32();
                var entryCount = reader.ReadInt32();
                var entries = ImmutableArray.CreateBuilder<CommandEntry>(initialCapacity: entryCount);

                for (var entryIndex = 0; (entryIndex < entryCount); entryIndex++) {
                    var recordedId = reader.ReadUInt16();
                    var phase = ((CommandPhase)reader.ReadByte());
                    var value = ReadValue(reader: reader);

                    // Remap by name so a recording survives a rebuild that reassigns ids; an entry naming a command
                    // the current build no longer interns is dropped rather than mis-bound. Device is not restored
                    // (it was never serialized — it is a local-only annotation).
                    if ((recordedId < names.Length) && registry.TryGetId(name: names[recordedId], id: out var currentId)) {
                        entries.Add(item: new CommandEntry(CommandId: currentId, Value: value, Phase: phase));
                    }
                }

                if (entries.Count == 0) {
                    continue;
                }

                // Re-sort by the CURRENT id so the lane keeps its deterministic, hashable layout after remapping.
                entries.Sort(comparison: static (left, right) => left.CommandId.CompareTo(value: right.CommandId));
                lanes.Add(item: new CommandLane(Entries: entries.DrainToImmutable(), Slot: slot));
            }

            lanes.Sort(comparison: static (left, right) => left.Slot.CompareTo(value: right.Slot));
            snapshots.Add(item: new CommandSnapshot(Lanes: lanes.DrainToImmutable(), Tick: tick));
        }

        return new SnapshotRecording { Seed = seed, Snapshots = snapshots.DrainToImmutable(), };
    }

    // The number of Vector4 components a value kind actually uses; the rest are always zero, so only these are stored.
    private static int ComponentCount(CommandValueKind kind) {
        return kind switch {
            CommandValueKind.Digital => 1,
            CommandValueKind.Axis1D => 1,
            CommandValueKind.Axis2D => 2,
            CommandValueKind.Axis3D => 3,
            CommandValueKind.Orientation => 4,
            _ => 4,
        };
    }
    private static void WriteValue(BinaryWriter writer, CommandValue value) {
        var count = ComponentCount(kind: value.Kind);
        var raw = value.Raw;

        writer.Write(value: ((byte)value.Kind));

        if (count > 0) { writer.Write(value: raw.X); }
        if (count > 1) { writer.Write(value: raw.Y); }
        if (count > 2) { writer.Write(value: raw.Z); }
        if (count > 3) { writer.Write(value: raw.W); }
    }
    private static CommandValue ReadValue(BinaryReader reader) {
        var kind = ((CommandValueKind)reader.ReadByte());
        var count = ComponentCount(kind: kind);
        var x = ((count > 0) ? reader.ReadSingle() : 0f);
        var y = ((count > 1) ? reader.ReadSingle() : 0f);
        var z = ((count > 2) ? reader.ReadSingle() : 0f);
        var w = ((count > 3) ? reader.ReadSingle() : 0f);

        return new CommandValue(Kind: kind, Raw: new Vector4(x: x, y: y, z: z, w: w));
    }
}
