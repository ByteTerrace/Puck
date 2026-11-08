using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    // An autonomous traveller's own World principal is durable authority state, never an actor admitted from a peer, so
    // the checkpoint spells it by a flag the wire's live principal leaf never carries.
    private static void WriteDurablePrincipal(WireWriter writer, Puck.Commands.Principal principal) {
        writer.WriteBoolean(value: (principal == Puck.Commands.Principal.World));
        if (principal != Puck.Commands.Principal.World) {
            WritePrincipal(
                principal: principal,
                writer: writer
            );
        }
    }
    private static Puck.Commands.Principal ReadDurablePrincipal(ref WireReader reader) =>
        (reader.ReadBoolean() ? Puck.Commands.Principal.World : WorldWireCodec.ReadPrincipal(reader: ref reader));
    private static void WriteReservationMember(WireWriter writer, WorldTransferReservationMember member) {
        WriteDurablePrincipal(
            principal: member.Principal,
            writer: writer
        );
        writer.WriteInt32(value: member.PreferredSlot);
        WorldIdentityProjectionWire.WriteOptional(
            projection: member.Identity,
            writer: writer
        );
        WorldWireLeaves.WriteIntentSource(
            writer: writer,
            source: member.Source
        );
        writer.WriteVector(value: member.BodyColor);
        writer.WriteByte(value: member.CatalogRig);
        writer.WriteOptional(
            value: member.Mobility,
            writeValue: WorldWireLeaves.WriteMobility
        );
        writer.WriteBoolean(value: member.RemoteHuman);
        WorldWireLeaves.WriteMemberIdentity(
            companion: member.Companion,
            memberId: member.MemberId,
            writer: writer
        );
    }
    private static WorldTransferReservationMember ReadReservationMember(ref WireReader reader) {
        var principal = ReadDurablePrincipal(reader: ref reader);
        var preferredSlot = reader.ReadInt32();
        var identity = WorldIdentityProjectionWire.ReadOptional(reader: ref reader);
        var source = WorldWireCodec.ReadIntentSource(reader: ref reader);
        var bodyColor = reader.ReadFiniteVector(field: "reservation member body color");
        var catalogRig = reader.ReadByte();
        var mobility = reader.ReadOptional(
            readValue: static (ref WireReader r) => WorldWireLeaves.ReadMobility(reader: ref r)
        );
        var remoteHuman = reader.ReadBoolean();
        var (memberId, companion) = WorldWireLeaves.ReadMemberIdentity(reader: ref reader);

        return new WorldTransferReservationMember(
            BodyColor: bodyColor,
            CatalogRig: catalogRig,
            Companion: companion,
            Identity: identity,
            MemberId: memberId,
            Mobility: mobility,
            PreferredSlot: preferredSlot,
            Principal: principal,
            RemoteHuman: remoteHuman,
            Source: source
        );
    }
    private static void WriteReservationRequest(WireWriter writer, WorldTransferReservationRequest request) {
        writer.WriteUInt64(value: request.TransferId);
        writer.WriteString(value: request.SourceAuthority);
        writer.WriteInt32(value: request.SourceRateHz);
        writer.WriteUInt64(value: request.SourceTick);
        writer.WriteUInt64(value: request.DeadlineSourceTick);
        writer.WriteString(value: request.Border);
        writer.WriteOptional(
            value: request.BorderCapacity,
            writeValue: static (w, v) => w.WriteInt32(value: v)
        );
        writer.WriteBoolean(value: request.PartyAllOrNothing);
        writer.WriteBoolean(value: request.PeerAdmission);
        writer.WriteArray(
            items: request.Members,
            writeItem: WriteReservationMember
        );
        writer.WriteUInt64(value: request.SourceFloor);
    }
    private static WorldTransferReservationRequest ReadReservationRequest(ref WireReader reader) {
        var transferId = reader.ReadUInt64();
        var sourceAuthority = reader.ReadString(
            field: "reservation source authority",
            maxBytes: MaxStringBytes
        );
        var sourceRateHz = reader.ReadInt32();
        var sourceTick = reader.ReadUInt64();
        var deadlineSourceTick = reader.ReadUInt64();
        var border = reader.ReadString(
            field: "reservation border",
            maxBytes: MaxStringBytes
        );
        var borderCapacity = reader.ReadOptional(
            readValue: static (ref WireReader r) => r.ReadInt32()
        );
        var partyAllOrNothing = reader.ReadBoolean();
        var peerAdmission = reader.ReadBoolean();
        var members = reader.ReadArray(
            field: "reservation members",
            readItem: (ref WireReader r) => ReadReservationMember(
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );
        var sourceFloor = reader.ReadUInt64();

        return new WorldTransferReservationRequest(
            Border: border,
            BorderCapacity: borderCapacity,
            DeadlineSourceTick: deadlineSourceTick,
            Members: members,
            PartyAllOrNothing: partyAllOrNothing,
            PeerAdmission: peerAdmission,
            SourceAuthority: sourceAuthority,
            SourceFloor: sourceFloor,
            SourceRateHz: sourceRateHz,
            SourceTick: sourceTick,
            TransferId: transferId
        );
    }
    private static void WriteLease(WireWriter writer, WorldTransferLeaseCheckpoint lease) {
        WorldWireLeaves.WriteTransferKey(
            writer: writer,
            key: lease.Key
        );
        WriteReservationRequest(
            writer: writer,
            request: lease.Request
        );
        writer.WriteUInt64(value: lease.DeadlineTick);
        writer.WriteArray(
            items: lease.Slots,
            writeItem: static (w, v) => w.WriteInt32(value: v)
        );
        writer.WriteBlock(value: lease.DestinationDefinitionJson);
        writer.WriteOptionalClass(
            value: lease.Arrival,
            writeValue: WriteAdmissionVerdict
        );
    }
    private static WorldTransferLeaseCheckpoint ReadLease(ref WireReader reader) {
        var key = WorldWireLeaves.ReadTransferKey(reader: ref reader);
        var request = ReadReservationRequest(
            reader: ref reader
        );
        var deadlineTick = reader.ReadUInt64();
        var slots = reader.ReadArray(
            field: "lease slots",
            readItem: static (ref WireReader r) => r.ReadInt32(),
            maximum: MaxCollectionCount
        );
        var destinationDefinitionJson = reader.ReadBlock(
            field: "lease destination definition",
            maxBytes: MaxSectionBytes
        );
        var arrival = reader.ReadOptionalClass(
            readValue: static (ref WireReader r) => ReadAdmissionVerdict(reader: ref r)
        );

        return new WorldTransferLeaseCheckpoint(
            Arrival: arrival,
            DeadlineTick: deadlineTick,
            DestinationDefinitionJson: destinationDefinitionJson,
            Key: key,
            Request: request,
            Slots: slots
        );
    }
    private static void WriteCommitted(WireWriter writer, WorldTransferCommittedCheckpoint committed) {
        WorldWireLeaves.WriteTransferKey(
            writer: writer,
            key: committed.Key
        );
        writer.WriteArray(
            items: committed.Members,
            writeItem: WorldWireLeaves.WriteCommitMember
        );
        writer.WriteArray(
            items: committed.Principals,
            writeItem: WriteDurablePrincipal
        );
        writer.WriteArray(
            items: committed.Incarnations,
            writeItem: WorldWireLeaves.WriteEntityAddress
        );
        writer.WriteArray(
            items: committed.Landed,
            writeItem: static (w, id) => w.WriteInt32(value: id)
        );
    }
    private static WorldTransferCommittedCheckpoint ReadCommitted(ref WireReader reader) {
        var key = WorldWireLeaves.ReadTransferKey(reader: ref reader);
        var members = reader.ReadArray(
            field: "committed members",
            readItem: (ref WireReader r) => WorldWireLeaves.ReadCommitMember(
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );
        var principals = reader.ReadArray(
            field: "committed principals",
            readItem: static (ref WireReader r) => ReadDurablePrincipal(reader: ref r),
            maximum: MaxCollectionCount
        );
        var incarnations = reader.ReadArray(
            field: "committed incarnations",
            readItem: static (ref WireReader r) => WorldWireLeaves.ReadEntityAddress(reader: ref r),
            maximum: MaxCollectionCount
        );
        var landed = reader.ReadArray(
            field: "committed landed members",
            readItem: static (ref WireReader r) => r.ReadInt32(),
            maximum: MaxCollectionCount
        );

        return new WorldTransferCommittedCheckpoint(
            Incarnations: incarnations,
            Key: key,
            Landed: landed,
            Members: members,
            Principals: principals
        );
    }
    private static byte[] EncodeEscrow(WorldTransferEscrowCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteArray(
            items: section.Leases,
            writeItem: WriteLease
        );
        writer.WriteArray(
            items: section.Committed,
            writeItem: WriteCommitted
        );
        writer.WriteArray(
            items: section.LatestCommittedTransfer,
            writeItem: static (w, row) => {
                WorldWireLeaves.WriteEntityAddress(
                    address: row.Incarnation,
                    writer: w
                );
                WorldWireLeaves.WriteTransferKey(
                    key: row.Transfer,
                    writer: w
                );
            }
        );
        writer.WriteArray(
            items: section.MobilityLeases,
            writeItem: static (w, row) => {
                WorldWireLeaves.WriteEntityAddress(
                    address: row.Incarnation,
                    writer: w
                );
                WorldWireLeaves.WriteTransferKey(
                    key: row.Transfer,
                    writer: w
                );
                w.WriteUInt64(value: row.ExpectedEpoch);
            }
        );
        writer.WriteArray(
            items: section.MobilityAdmissions,
            writeItem: static (w, row) => {
                w.WriteString(value: row.SourceAuthority);
                WorldWireLeaves.WriteEntityAddress(
                    address: row.Incarnation,
                    writer: w
                );
                w.WriteUInt64(value: row.Epoch);
                WriteDurablePrincipal(
                    principal: row.Principal,
                    writer: w
                );
            }
        );
        writer.WriteArray(
            items: section.BorderAdmissions,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.Slot);
                w.WriteString(value: row.Border);
            }
        );
        writer.WriteUInt64(value: section.CrossingSequence);
        writer.WriteArray(
            items: section.SpentKeys,
            writeItem: static (w, row) => {
                WorldWireLeaves.WriteTransferKey(
                    key: row.Key,
                    writer: w
                );
                w.WriteBoolean(value: row.Committed);
                w.WriteBoolean(value: row.Durable);
            }
        );
        writer.WriteArray(
            items: section.Floors,
            writeItem: static (w, row) => {
                w.WriteString(value: row.SourceAuthority);
                w.WriteUInt64(value: row.Floor);
            }
        );
        writer.WriteArray(
            items: section.Custody,
            writeItem: static (w, record) => WriteCrossingRecord(
                record: record,
                writer: w
            )
        );

        return writer.ToArray();
    }
    private static bool TryDecodeEscrow(byte[] bytes, out string reason, out WorldTransferEscrowCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var leases = reader.ReadArray(
            field: "escrow leases",
            readItem: (ref WireReader r) => ReadLease(
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );
        var committed = reader.ReadArray(
            field: "escrow committed",
            readItem: (ref WireReader r) => ReadCommitted(
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );
        var latest = reader.ReadArray(
            field: "escrow latest committed transfer",
            readItem: static (ref WireReader r) => {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref r);
                var transfer = WorldWireLeaves.ReadTransferKey(reader: ref r);

                return (incarnation, transfer);
            },
            maximum: MaxCollectionCount
        );
        var mobilityLeases = reader.ReadArray(
            field: "escrow mobility leases",
            readItem: static (ref WireReader r) => {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref r);
                var transfer = WorldWireLeaves.ReadTransferKey(reader: ref r);
                var expectedEpoch = r.ReadUInt64();

                return (incarnation, transfer, expectedEpoch);
            },
            maximum: MaxCollectionCount
        );
        var mobilityAdmissions = reader.ReadArray(
            field: "escrow mobility admissions",
            readItem: static (ref WireReader r) => {
                var sourceAuthority = r.ReadString(
                    field: "mobility admission source authority",
                    maxBytes: MaxStringBytes
                );
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref r);
                var epoch = r.ReadUInt64();
                var principal = ReadDurablePrincipal(reader: ref r);

                return (sourceAuthority, incarnation, epoch, principal);
            },
            maximum: MaxCollectionCount
        );
        var borderAdmissions = reader.ReadArray(
            field: "escrow border admissions",
            readItem: static (ref WireReader r) => {
                var slot = r.ReadInt32();
                var border = r.ReadString(
                    field: "border admission border",
                    maxBytes: MaxStringBytes
                );

                return (slot, border);
            },
            maximum: MaxCollectionCount
        );
        var crossingSequence = reader.ReadUInt64();
        var spentKeys = reader.ReadArray(
            field: "escrow spent transfer keys",
            readItem: static (ref WireReader r) => {
                var key = WorldWireLeaves.ReadTransferKey(reader: ref r);
                var committed = r.ReadBoolean();
                var durable = r.ReadBoolean();

                return (key, committed, durable);
            },
            maximum: MaxCollectionCount
        );
        var floors = reader.ReadArray(
            field: "escrow source floors",
            readItem: static (ref WireReader r) => {
                var sourceAuthority = r.ReadString(
                    field: "source floor authority",
                    maxBytes: MaxStringBytes
                );
                var floor = r.ReadUInt64();

                return (sourceAuthority, floor);
            },
            maximum: MaxCollectionCount
        );
        var custody = reader.ReadArray(
            field: "escrow custody history",
            readItem: static (ref WireReader r) => {
                var record = ReadCrossingRecord(reader: ref r);

                if (!r.Failed && ((record is null) || !WorldCompanionCustody.Folds(record: record))) {
                    r.Fail(
                        detail: "escrow custody history carries a record the custody reducer does not fold",
                        refusal: WireRefusal.PayloadMalformed
                    );
                }
                return record!;
            },
            maximum: MaxCollectionCount
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"escrow section: {failure}";

            return false;
        }

        section = new WorldTransferEscrowCheckpoint(
            BorderAdmissions: borderAdmissions,
            Committed: committed,
            CrossingSequence: crossingSequence,
            Custody: custody,
            Floors: floors,
            LatestCommittedTransfer: latest,
            Leases: leases,
            MobilityAdmissions: mobilityAdmissions,
            MobilityLeases: mobilityLeases,
            SpentKeys: spentKeys
        );
        reason = string.Empty;

        return true;
    }
    // ---- input hold section ----

}
