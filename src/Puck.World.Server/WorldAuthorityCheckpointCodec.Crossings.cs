using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private const byte CrossingArrivalTag = 0;
    private const byte CrossingDepartureTag = 1;
    private const byte CrossingSettlementTag = 2;
    private const byte CrossingLeaseTag = 3;
    private const byte CrossingTombstoneTag = 4;
    private const byte CrossingAllocationTag = 5;
    private const byte CrossingHopTag = 6;
    private const byte CrossingHopDoneTag = 7;
    private const byte CrossingHopAckedTag = 8;
    private const byte CrossingRecallTag = 9;
    private const byte CrossingNoticedTag = 10;
    private const byte CrossingReleaseTag = 11;
    private const byte CrossingRetireTag = 12;
    private const byte CrossingStrandedTag = 13;
    private const byte CrossingGraceElapsedTag = 14;

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
            writeItem: WorldWireLeaves.WriteCommitMember
        );
        writer.WriteArray(
            items: arrival.Readopted,
            writeItem: WorldWireLeaves.WriteEntityAddress
        );
        writer.WriteArray(
            items: arrival.Generations,
            writeItem: static (w, generation) => w.WriteInt32(value: generation)
        );
        writer.WriteArray(
            items: arrival.Leased,
            writeItem: static (w, id) => w.WriteInt32(value: id)
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
            readItem: static (ref WireReader r) => WorldWireLeaves.ReadCommitMember(reader: ref r),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        var readopted = reader.ReadArray(
            field: "arrival readopted companions",
            readItem: static (ref WireReader r) => WorldWireLeaves.ReadEntityAddress(reader: ref r),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        var generations = reader.ReadArray(
            field: "arrival generations",
            readItem: static (ref WireReader r) => r.ReadInt32(),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        var leased = reader.ReadArray(
            field: "arrival leased ids",
            readItem: static (ref WireReader r) => r.ReadInt32(),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        var arrival = new WorldCrossingArrival(
            Generations: generations,
            Leased: leased,
            Members: members,
            Readopted: readopted,
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
        WriteCrossingRecord(
            record: entry.Record,
            writer: writer
        );

        return writer.ToArray();
    }
    /// <summary>Encodes one crossing record alone: the leaf a crossing-log entry, an escrow checkpoint's custody history
    /// and a replay tape's custody entry share.</summary>
    /// <param name="record">The record to encode.</param>
    /// <returns>The encoded record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeCrossingRecord(WorldCrossingRecord record) {
        ArgumentNullException.ThrowIfNull(argument: record);

        var writer = new WireWriter();

        WriteCrossingRecord(
            record: record,
            writer: writer
        );

        return writer.ToArray();
    }
    /// <summary>Decodes one crossing record encoded by <see cref="EncodeCrossingRecord"/>.</summary>
    /// <param name="bytes">The encoded record.</param>
    /// <param name="record">The decoded record on success.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the bytes decoded exactly.</returns>
    public static bool TryDecodeCrossingRecord(ReadOnlySpan<byte> bytes, out WorldCrossingRecord? record, out string reason) {
        var reader = new WireReader(bytes: bytes);
        var decoded = ReadCrossingRecord(reader: ref reader);

        if (
            !reader.TryFinish(failure: out var failure) ||
            (decoded is null)
        ) {
            record = null;
            reason = $"crossing record: {failure}";

            return false;
        }

        record = decoded;
        reason = string.Empty;

        return true;
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
        var record = (reader.Failed
            ? null
            : ReadCrossingRecord(reader: ref reader));

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

    private static void WriteCustodyHopMember(WireWriter writer, WorldCustodyHopMember member) {
        WorldWireLeaves.WriteEntityAddress(
            address: member.Incarnation,
            writer: writer
        );
        writer.WriteUInt64(value: member.Epoch);
        writer.WriteUInt64(value: member.Next);
    }
    private static WorldCustodyHopMember ReadCustodyHopMember(ref WireReader reader) {
        var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
        var epoch = reader.ReadUInt64();
        var next = reader.ReadUInt64();

        if (!reader.Failed && (next != (epoch + 1UL))) {
            reader.Fail(
                detail: $"hop member lands at epoch {next}, not one above {epoch}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return new WorldCustodyHopMember(
            Epoch: epoch,
            Incarnation: incarnation,
            Next: next
        );
    }
    private static void WriteCrossingRecord(WireWriter writer, WorldCrossingRecord record) {
        switch (record) {
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
                writer.WriteArray(
                    items: settlement.Landed,
                    writeItem: static (w, id) => w.WriteInt32(value: id)
                );
                writer.WriteArray(
                    items: settlement.Retired,
                    writeItem: static (w, id) => w.WriteInt32(value: id)
                );
                writer.WriteArray(
                    items: settlement.Forwarded,
                    writeItem: WriteForwardedBody
                );
                break;
            case WorldCrossingRecord.Lease lease:
                writer.WriteByte(value: CrossingLeaseTag);
                WorldWireLeaves.WriteTransferKey(
                    key: lease.Key,
                    writer: writer
                );
                writer.WriteUInt64(value: lease.Floor);
                break;
            case WorldCrossingRecord.Tombstone tombstone:
                writer.WriteByte(value: CrossingTombstoneTag);
                WorldWireLeaves.WriteTransferKey(
                    key: tombstone.Key,
                    writer: writer
                );
                writer.WriteArray(
                    items: tombstone.Burned,
                    writeItem: static (w, slot) => w.WriteInt32(value: slot)
                );
                break;
            case WorldCrossingRecord.Allocation allocation:
                writer.WriteByte(value: CrossingAllocationTag);
                writer.WriteUInt64(value: allocation.Next);
                writer.WriteOptionalClass(
                    value: allocation.Mint,
                    writeValue: static (w, mint) => {
                        w.WriteUInt64(value: mint.TransferId);
                        w.WriteString(value: mint.To);
                        w.WriteArray(
                            items: mint.Companions,
                            writeItem: static (m, companion) => {
                                WorldWireLeaves.WriteEntityAddress(address: companion.Incarnation, writer: m);
                                m.WriteString(value: companion.Home);
                            }
                        );
                    }
                );
                break;
            case WorldCrossingRecord.Hop hop:
                writer.WriteByte(value: CrossingHopTag);
                WorldWireLeaves.WriteTransferKey(
                    key: hop.Key,
                    writer: writer
                );
                writer.WriteString(value: hop.From);
                writer.WriteString(value: hop.To);
                writer.WriteArray(
                    items: hop.Members,
                    writeItem: WriteCustodyHopMember
                );
                break;
            case WorldCrossingRecord.HopDone done:
                writer.WriteByte(value: CrossingHopDoneTag);
                WorldWireLeaves.WriteTransferKey(
                    key: done.Key,
                    writer: writer
                );
                WorldWireLeaves.WriteEntityAddress(
                    address: done.Incarnation,
                    writer: writer
                );
                writer.WriteBoolean(value: done.Arrived);
                break;
            case WorldCrossingRecord.HopAcked acked:
                writer.WriteByte(value: CrossingHopAckedTag);
                WorldWireLeaves.WriteTransferKey(
                    key: acked.Key,
                    writer: writer
                );
                WorldWireLeaves.WriteEntityAddress(
                    address: acked.Incarnation,
                    writer: writer
                );
                break;
            case WorldCrossingRecord.Recall recall:
                writer.WriteByte(value: CrossingRecallTag);
                WorldWireLeaves.WriteEntityAddress(
                    address: recall.Incarnation,
                    writer: writer
                );
                writer.WriteUInt64(value: recall.Epoch);
                break;
            case WorldCrossingRecord.Noticed noticed:
                writer.WriteByte(value: CrossingNoticedTag);
                WorldWireLeaves.WriteEntityAddress(
                    address: noticed.Incarnation,
                    writer: writer
                );
                writer.WriteByte(value: (byte)noticed.What);
                writer.WriteUInt64(value: noticed.Epoch);
                break;
            case WorldCrossingRecord.Release release:
                writer.WriteByte(value: CrossingReleaseTag);
                WorldWireLeaves.WriteEntityAddress(
                    address: release.Incarnation,
                    writer: writer
                );
                break;
            case WorldCrossingRecord.Retire retire:
                writer.WriteByte(value: CrossingRetireTag);
                WorldWireLeaves.WriteEntityAddress(
                    address: retire.Incarnation,
                    writer: writer
                );
                writer.WriteByte(value: (byte)retire.Cause);
                break;
            case WorldCrossingRecord.Stranded stranded:
                writer.WriteByte(value: CrossingStrandedTag);
                WorldWireLeaves.WriteEntityAddress(
                    address: stranded.Incarnation,
                    writer: writer
                );
                writer.WriteInt32(value: stranded.Visit);
                writer.WriteUInt64(value: stranded.DeadlineTick);
                break;
            case WorldCrossingRecord.GraceElapsed grace:
                writer.WriteByte(value: CrossingGraceElapsedTag);
                WorldWireLeaves.WriteEntityAddress(
                    address: grace.Incarnation,
                    writer: writer
                );
                writer.WriteInt32(value: grace.Visit);
                break;
            default:
                throw new InvalidOperationException(message: $"no crossing-log leaf for record kind '{record.GetType().Name}'");
        }
    }
    private static WorldCrossingRecord? ReadCrossingRecord(ref WireReader reader) {
        var tag = reader.ReadByte();

        if (reader.Failed) {
            return null;
        }
        switch (tag) {
            case CrossingArrivalTag:
                return new WorldCrossingRecord.Arrival(Value: ReadCrossingArrival(reader: ref reader));
            case CrossingDepartureTag:
                return new WorldCrossingRecord.Departure(Transfer: ReadInDoubtTransfer(
                    reader: ref reader
                ));
            case CrossingSettlementTag: {
                var transferId = reader.ReadUInt64();
                var landed = reader.ReadArray(
                    field: "settlement landed members",
                    readItem: static (ref WireReader r) => r.ReadInt32(),
                    maximum: WorldBodiesLimits.CapacityCeiling
                );
                var retired = reader.ReadArray(
                    field: "settlement retired members",
                    readItem: static (ref WireReader r) => r.ReadInt32(),
                    maximum: WorldBodiesLimits.CapacityCeiling
                );
                var forwarded = reader.ReadArray(
                    field: "settlement forwarded bodies",
                    readItem: static (ref WireReader r) => ReadForwardedBody(reader: ref r),
                    maximum: WorldBodiesLimits.CapacityCeiling
                );

                return new WorldCrossingRecord.Settlement(
                    Forwarded: forwarded,
                    Landed: landed,
                    Retired: retired,
                    TransferId: transferId
                );
            }
            case CrossingLeaseTag: {
                var key = WorldWireLeaves.ReadTransferKey(reader: ref reader);

                return new WorldCrossingRecord.Lease(
                    Floor: reader.ReadUInt64(),
                    Key: key
                );
            }
            case CrossingTombstoneTag: {
                var key = WorldWireLeaves.ReadTransferKey(reader: ref reader);

                return new WorldCrossingRecord.Tombstone(
                    Burned: reader.ReadArray(
                        field: "tombstone burned slots",
                        readItem: static (ref WireReader r) => r.ReadInt32(),
                        maximum: WorldBodiesLimits.CapacityCeiling
                    ),
                    Key: key
                );
            }
            case CrossingAllocationTag: {
                var next = reader.ReadUInt64();

                return new WorldCrossingRecord.Allocation(
                    Mint: reader.ReadOptionalClass(readValue: static (ref WireReader r) => {
                        var transferId = r.ReadUInt64();
                        var to = r.ReadString(field: "mint destination authority", maxBytes: MaxStringBytes);

                        return new WorldTransferMint(
                            Companions: r.ReadArray(
                                field: "mint companions",
                                readItem: static (ref WireReader m) => {
                                    var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref m);

                                    return new WorldCustodyMintMember(
                                        Home: m.ReadString(field: "mint companion home", maxBytes: MaxStringBytes),
                                        Incarnation: incarnation
                                    );
                                },
                                maximum: WorldBodiesLimits.CapacityCeiling
                            ),
                            To: to,
                            TransferId: transferId
                        );
                    }),
                    Next: next
                );
            }
            case CrossingHopTag: {
                var key = WorldWireLeaves.ReadTransferKey(reader: ref reader);
                var from = reader.ReadString(
                    field: "hop source authority",
                    maxBytes: MaxStringBytes
                );
                var to = reader.ReadString(
                    field: "hop destination authority",
                    maxBytes: MaxStringBytes
                );
                var members = reader.ReadArray(
                    field: "hop members",
                    readItem: static (ref WireReader r) => ReadCustodyHopMember(reader: ref r),
                    maximum: WorldBodiesLimits.CapacityCeiling
                );

                return new WorldCrossingRecord.Hop(
                    From: from,
                    Key: key,
                    Members: members,
                    To: to
                );
            }
            case CrossingHopDoneTag: {
                var key = WorldWireLeaves.ReadTransferKey(reader: ref reader);
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);

                return new WorldCrossingRecord.HopDone(
                    Arrived: reader.ReadBoolean(),
                    Incarnation: incarnation,
                    Key: key
                );
            }
            case CrossingHopAckedTag: {
                var key = WorldWireLeaves.ReadTransferKey(reader: ref reader);

                return new WorldCrossingRecord.HopAcked(
                    Incarnation: WorldWireLeaves.ReadEntityAddress(reader: ref reader),
                    Key: key
                );
            }
            case CrossingRecallTag: {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);

                return new WorldCrossingRecord.Recall(
                    Epoch: reader.ReadUInt64(),
                    Incarnation: incarnation
                );
            }
            case CrossingNoticedTag: {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
                var what = reader.ReadByte();

                if (!reader.Failed && !Enum.IsDefined(value: (WorldCustodyNotice)what)) {
                    reader.Fail(
                        detail: $"custody notice {what} is not declared",
                        refusal: WireRefusal.EnumValueUnknown
                    );
                }

                return new WorldCrossingRecord.Noticed(
                    Epoch: reader.ReadUInt64(),
                    Incarnation: incarnation,
                    What: (WorldCustodyNotice)what
                );
            }
            case CrossingReleaseTag:
                return new WorldCrossingRecord.Release(Incarnation: WorldWireLeaves.ReadEntityAddress(reader: ref reader));
            case CrossingRetireTag: {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
                var cause = reader.ReadByte();

                if (!reader.Failed && !Enum.IsDefined(value: (WorldCustodyRetireCause)cause)) {
                    reader.Fail(
                        detail: $"retire cause {cause} is not declared",
                        refusal: WireRefusal.EnumValueUnknown
                    );
                }

                return new WorldCrossingRecord.Retire(
                    Cause: (WorldCustodyRetireCause)cause,
                    Incarnation: incarnation
                );
            }
            case CrossingStrandedTag: {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
                var visit = reader.ReadInt32();

                return new WorldCrossingRecord.Stranded(
                    DeadlineTick: reader.ReadUInt64(),
                    Incarnation: incarnation,
                    Visit: visit
                );
            }
            case CrossingGraceElapsedTag: {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);

                return new WorldCrossingRecord.GraceElapsed(
                    Incarnation: incarnation,
                    Visit: reader.ReadInt32()
                );
            }
            default:
                reader.Fail(
                    detail: $"crossing record tag {tag} is not declared",
                    refusal: WireRefusal.EnumValueUnknown
                );
                return null;
        }
    }
}
