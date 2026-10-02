using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private const byte CrossingArrivalTag = 0;
    private const byte CrossingDepartureTag = 1;
    private const byte CrossingSettlementTag = 2;

    private static void WriteCrossingArrival(WireWriter writer, WorldCrossingArrival arrival) {
        WriteReservationRequest(
            request: arrival.Request,
            writer: writer
        );
        writer.WriteArray(
            items: arrival.Slots,
            writeItem: static (w, slot) => w.WriteInt32(value: slot)
        );
        writer.WriteArray(
            items: arrival.Members,
            writeItem: WriteCommitMember
        );
    }
    private static WorldCrossingArrival ReadCrossingArrival(ref WireReader reader) {
        var request = ReadReservationRequest(reader: ref reader);
        var slots = reader.ReadArray(
            field: "arrival slots",
            readItem: static (ref WireReader r) => r.ReadInt32(),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        var members = reader.ReadArray(
            field: "arrival members",
            readItem: static (ref WireReader r) => ReadCommitMember(reader: ref r),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        var arrival = new WorldCrossingArrival(
            Members: members,
            Request: request,
            Slots: slots
        );

        if (!reader.Failed && (ArrivalFault(arrival: arrival) is { } fault)) {
            reader.Fail(
                detail: fault,
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return arrival;
    }
    // Refuses an arrival no commit could have landed: a cohort whose body indices and members do not pair with its
    // travelers, an index outside every world's capacity or named twice, a traveler with no mobility identity, or a
    // member whose carried motion has no sound shape.
    private static string? ArrivalFault(WorldCrossingArrival arrival) {
        var count = arrival.Request.Members.Count;

        if (count == 0) {
            return "arrival binds no traveler";
        }
        if (
            (arrival.Slots.Count != count) ||
            (arrival.Members.Count != count)
        ) {
            return $"arrival binds {count} traveler(s) but carries {arrival.Slots.Count} slot(s) and {arrival.Members.Count} member(s)";
        }
        for (var index = 0; (index < count); index++) {
            var slot = arrival.Slots[index];

            if (((uint)slot) >= WorldBodiesLimits.CapacityCeiling) {
                return $"arrival traveler {(index + 1)} lands at body:{slot}, outside 0..{(WorldBodiesLimits.CapacityCeiling - 1)}";
            }
            for (var earlier = 0; (earlier < index); earlier++) {
                if (arrival.Slots[earlier] == slot) {
                    return $"arrival lands two travelers at body:{slot}";
                }
            }
            if (arrival.Request.Members[index].Mobility is null) {
                return $"arrival traveler {(index + 1)} carries no mobility identity";
            }
            if (WorldTransferEscrow.CommitMemberFault(member: arrival.Members[index]) is { } fault) {
                return $"arrival traveler {(index + 1)} {fault}";
            }
        }
        return null;
    }

    /// <summary>Encodes one destination arrival — the leaf a destination tape and a crossing-log arrival record
    /// share.</summary>
    /// <param name="arrival">The arrival to encode.</param>
    /// <returns>The encoded arrival.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arrival"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeCrossingArrival(WorldCrossingArrival arrival) {
        ArgumentNullException.ThrowIfNull(argument: arrival);

        var writer = new WireWriter();

        WriteCrossingArrival(
            arrival: arrival,
            writer: writer
        );

        return writer.ToArray();
    }
    /// <summary>Decodes one destination arrival encoded by <see cref="EncodeCrossingArrival"/>.</summary>
    /// <param name="bytes">The encoded arrival.</param>
    /// <param name="arrival">The decoded arrival on success.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the bytes decoded exactly.</returns>
    public static bool TryDecodeCrossingArrival(ReadOnlySpan<byte> bytes, out WorldCrossingArrival? arrival, out string reason) {
        var reader = new WireReader(bytes: bytes);
        var decoded = ReadCrossingArrival(reader: ref reader);

        if (!reader.TryFinish(failure: out var failure)) {
            arrival = null;
            reason = $"crossing arrival: {failure}";

            return false;
        }

        arrival = decoded;
        reason = string.Empty;

        return true;
    }
    /// <summary>Encodes one crossing-log entry: its sequence, its tick, and its record.</summary>
    /// <param name="entry">The entry to encode.</param>
    /// <returns>The encoded entry.</returns>
    /// <exception cref="ArgumentNullException">The entry's record is <see langword="null"/>.</exception>
    public static byte[] EncodeCrossingEntry(in WorldCrossingEntry entry) {
        ArgumentNullException.ThrowIfNull(argument: entry.Record);

        var writer = new WireWriter();

        writer.WriteUInt64(value: entry.Sequence);
        writer.WriteUInt64(value: entry.Tick);
        switch (entry.Record) {
            case WorldCrossingRecord.Arrival arrival:
                writer.WriteByte(value: CrossingArrivalTag);
                WriteCrossingArrival(
                    arrival: arrival.Value,
                    writer: writer
                );
                break;
            case WorldCrossingRecord.Departure departure:
                writer.WriteByte(value: CrossingDepartureTag);
                WriteInDoubtTransfer(
                    row: departure.Transfer,
                    writer: writer
                );
                break;
            case WorldCrossingRecord.Settlement settlement:
                writer.WriteByte(value: CrossingSettlementTag);
                writer.WriteUInt64(value: settlement.TransferId);
                writer.WriteBoolean(value: settlement.Arrived);
                writer.WriteArray(
                    items: settlement.Forwarded,
                    writeItem: WriteForwardedBody
                );
                break;
            default:
                throw new InvalidOperationException(message: $"no crossing-log leaf for record kind '{entry.Record.GetType().Name}'");
        }

        return writer.ToArray();
    }
    /// <summary>Decodes one crossing-log entry encoded by <see cref="EncodeCrossingEntry"/>.</summary>
    /// <param name="bytes">The encoded entry.</param>
    /// <param name="entry">The decoded entry on success.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the bytes decoded exactly.</returns>
    public static bool TryDecodeCrossingEntry(ReadOnlySpan<byte> bytes, out WorldCrossingEntry entry, out string reason) {

        var reader = new WireReader(bytes: bytes);
        var sequence = reader.ReadUInt64();
        var tick = reader.ReadUInt64();
        var tag = reader.ReadByte();
        WorldCrossingRecord? record = null;

        if (!reader.Failed) {
            switch (tag) {
                case CrossingArrivalTag:
                    record = new WorldCrossingRecord.Arrival(Value: ReadCrossingArrival(reader: ref reader));
                    break;
                case CrossingDepartureTag:
                    record = new WorldCrossingRecord.Departure(Transfer: ReadInDoubtTransfer(
                        reader: ref reader
                    ));
                    break;
                case CrossingSettlementTag: {
                        var transferId = reader.ReadUInt64();
                        var arrived = reader.ReadBoolean();
                        var forwarded = reader.ReadArray(
                            field: "settlement forwarded bodies",
                            readItem: static (ref WireReader r) => ReadForwardedBody(reader: ref r),
                            maximum: WorldBodiesLimits.CapacityCeiling
                        );

                        record = new WorldCrossingRecord.Settlement(
                            Arrived: arrived,
                            Forwarded: forwarded,
                            TransferId: transferId
                        );
                        break;
                    }
                default:
                    reader.Fail(
                        detail: $"crossing record tag {tag} is not declared",
                        refusal: WireRefusal.EnumValueUnknown
                    );
                    break;
            }
        }

        if (
            !reader.TryFinish(failure: out var failure) ||
            (record is null)
        ) {
            entry = default;
            reason = $"crossing entry: {failure}";

            return false;
        }

        entry = new WorldCrossingEntry(
            Record: record,
            Sequence: sequence,
            Tick: tick
        );
        reason = string.Empty;

        return true;
    }
}
