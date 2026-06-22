using System.Collections.Immutable;
using System.Text;
using Puck.Commands;
using Puck.Demo.Replay;

namespace Puck.Demo.MiniAction;

/// <summary>
/// A complete MiniAction session recording: the engine's per-tick <see cref="CommandSnapshot"/> input stream — a
/// general <see cref="SnapshotRecording"/>, the cross-machine unit a peer transmits and the recorder stores — PLUS
/// MiniAction's own per-tick roster events (joins/leaves), the one game-specific channel the engine recorder does
/// not own. Because the sim is a pure function of <c>(seed, input snapshots, roster events)</c>, replaying both
/// channels reproduces a session bit-for-bit through join, leave, and slot recycling. This is the collapse of the
/// old bespoke per-tick <c>PlayerIntent</c> log onto the engine snapshot seam: input now rides
/// <see cref="SnapshotRecording"/>; only the roster stays here, where the game owns it.
/// </summary>
/// <remarks>The roster channel keeps this type in <c>Puck.Demo</c>; the input channel (<see cref="SnapshotRecording"/>)
/// is the part destined for a future <c>Puck.Replay</c>.</remarks>
public sealed class MiniActionRecording {
    private const uint Magic = 0x4D41_5253u; // "MARS"
    private const uint Version = 1u;

    /// <summary>The engine input channel: the per-tick command snapshots + seed.</summary>
    public required SnapshotRecording Input { get; init; }
    /// <summary>The per-tick roster events (joins/leaves), parallel to <see cref="SnapshotRecording.Snapshots"/>.</summary>
    public required ImmutableArray<ImmutableArray<RosterEvent>> RosterEvents { get; init; }

    /// <summary>The roster events applied at the start of <paramref name="tick"/>; empty past the end.</summary>
    /// <param name="tick">The tick to look up.</param>
    /// <returns>The tick's roster events.</returns>
    public IReadOnlyList<RosterEvent> RosterEventsForTick(ulong tick) {
        return ((tick < (ulong)RosterEvents.Length) ? RosterEvents[(int)tick] : []);
    }

    /// <summary>Serializes the recording: the engine input channel, then the roster channel.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="registry">The registry whose interned ids the input snapshots reference.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public void Write(Stream stream, CommandRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        ArgumentNullException.ThrowIfNull(argument: stream);

        SnapshotRecording.Write(stream: stream, recording: Input, registry: registry);

        using var writer = new BinaryWriter(output: stream, encoding: Encoding.UTF8, leaveOpen: true);

        writer.Write(value: Magic);
        writer.Write(value: Version);
        writer.Write(value: RosterEvents.Length);

        foreach (var perTick in RosterEvents) {
            writer.Write(value: perTick.Length);

            foreach (var rosterEvent in perTick) {
                writer.Write(value: ((byte)rosterEvent.Kind));
                writer.Write(buffer: rosterEvent.PlayerId.ToByteArray());
                writer.Write(value: rosterEvent.Slot);
            }
        }
    }

    /// <summary>Reads a recording: the engine input channel, then the roster channel.</summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="registry">The current registry the input snapshots are remapped into.</param>
    /// <returns>The deserialized recording.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The roster channel is not a MiniAction recording or is an unsupported version.</exception>
    public static MiniActionRecording Read(Stream stream, CommandRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        ArgumentNullException.ThrowIfNull(argument: stream);

        var input = SnapshotRecording.Read(stream: stream, registry: registry);

        using var reader = new BinaryReader(input: stream, encoding: Encoding.UTF8, leaveOpen: true);

        if ((reader.ReadUInt32() != Magic) || (reader.ReadUInt32() != Version)) {
            throw new InvalidDataException(message: "Not a MiniAction recording, or an unsupported version.");
        }

        var tickCount = reader.ReadInt32();
        var roster = ImmutableArray.CreateBuilder<ImmutableArray<RosterEvent>>(initialCapacity: tickCount);

        for (var tick = 0; (tick < tickCount); tick++) {
            var count = reader.ReadInt32();
            var events = ImmutableArray.CreateBuilder<RosterEvent>(initialCapacity: count);

            for (var index = 0; (index < count); index++) {
                var kind = ((RosterEventKind)reader.ReadByte());
                var playerId = new Guid(b: reader.ReadBytes(count: 16));
                var slot = reader.ReadInt32();

                events.Add(item: new RosterEvent(Kind: kind, PlayerId: playerId, Slot: slot));
            }

            roster.Add(item: events.DrainToImmutable());
        }

        return new MiniActionRecording { Input = input, RosterEvents = roster.DrainToImmutable(), };
    }
}
