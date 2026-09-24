using Puck.Networking;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteTransferKey(WireWriter writer, WorldTransferKey key) {
        writer.WriteString(value: key.SourceAuthority);
        writer.WriteUInt64(value: key.TransferId);
    }
    private static WorldTransferKey ReadTransferKey(ref WireReader reader) => new(
        SourceAuthority: reader.ReadString(
            field: "transfer key source authority",
            maxBytes: MaxStringBytes
        ),
        TransferId: reader.ReadUInt64()
    );
    private static void WriteContinuum(WireWriter writer, WorldContinuumTrajectory continuum) {
        writer.WriteFixedVector(value: continuum.PreviousPosition);
        writer.WriteUInt64(value: continuum.SourceTick);
        writer.WriteUInt64(value: continuum.ContinuumStartEngineTick);
        writer.WriteUInt64(value: continuum.ContinuumEndEngineTick);
        writer.WriteUInt64(value: continuum.ConsumedThroughEngineTick);
        writer.WriteByte(value: continuum.BoundaryEvents);
    }
    private static WorldContinuumTrajectory ReadContinuum(ref WireReader reader) => new(
        PreviousPosition: reader.ReadFixedVector(),
        SourceTick: reader.ReadUInt64(),
        ContinuumStartEngineTick: reader.ReadUInt64(),
        ContinuumEndEngineTick: reader.ReadUInt64(),
        ConsumedThroughEngineTick: reader.ReadUInt64(),
        BoundaryEvents: reader.ReadByte()
    );
    private static void WriteChannelEdge(WireWriter writer, WorldTransferChannelEdge edge) {
        writer.WriteString(value: edge.Name);
        writer.WriteBoolean(value: edge.PreviousBit);
        writer.WriteFixed(value: edge.HeldValue);
    }
    private static WorldTransferChannelEdge ReadChannelEdge(ref WireReader reader) => new(
        Name: reader.ReadString(
            field: "channel edge name",
            maxBytes: MaxStringBytes
        ),
        PreviousBit: reader.ReadBoolean(),
        HeldValue: reader.ReadFixed()
    );
    private static void WriteActionRegister(WireWriter writer, WorldTransferActionRegister register) {
        writer.WriteString(value: register.Name);
        writer.WriteByte(value: ((byte)register.Kind));
        writer.WriteFixed(value: register.Value);
        writer.WriteUInt64(value: register.TimerTicks);
    }
    private static WorldTransferActionRegister ReadActionRegister(ref WireReader reader) {
        var name = reader.ReadString(
            field: "action register name",
            maxBytes: MaxStringBytes
        );
        var kind = ((ActionStateKind)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: kind)
        ) {
            reader.Fail(
                detail: $"{nameof(ActionStateKind)} wire value {((byte)kind)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        var value = reader.ReadFixed();
        var timerTicks = reader.ReadUInt64();

        return new WorldTransferActionRegister(
            Kind: kind,
            Name: name,
            TimerTicks: timerTicks,
            Value: value
        );
    }
    private static void WriteActionContinuity(WireWriter writer, WorldTransferActionContinuity continuity) {
        writer.WriteArray(
            items: continuity.Channels,
            writeItem: WriteChannelEdge
        );
        writer.WriteArray(
            items: continuity.Registers,
            writeItem: WriteActionRegister
        );
    }
    private static WorldTransferActionContinuity ReadActionContinuity(ref WireReader reader) {
        var channels = reader.ReadArray(
            field: "action continuity channels",
            readItem: static (ref WireReader r) => ReadChannelEdge(reader: ref r),
            maximum: MaxCollectionCount
        );
        var registers = reader.ReadArray(
            field: "action continuity registers",
            readItem: static (ref WireReader r) => ReadActionRegister(reader: ref r),
            maximum: MaxCollectionCount
        );

        return new WorldTransferActionContinuity(
            Channels: channels,
            Registers: registers
        );
    }
    private static void WriteCommitMember(WireWriter writer, WorldTransferCommitMember member) {
        WriteIdentityOptional(
            writer: writer,
            identity: member.Profile
        );
        writer.WriteBoolean(value: member.HasMappedArrival);
        writer.WriteString(value: member.BodyMotionProgramName);
        writer.WriteFixedVector(value: member.Position);
        writer.WriteFixed(value: member.YawRadians);
        writer.WriteFixedVector(value: member.PlanarVelocity);
        writer.WriteFixed(value: member.VerticalVelocity);
        writer.WriteOptionalClass(
            value: member.ActionContinuity,
            writeValue: WriteActionContinuity
        );
        writer.WriteOptional(
            value: member.Continuum,
            writeValue: WriteContinuum
        );
    }
    private static WorldTransferCommitMember ReadCommitMember(ref WireReader reader, WorldPlayerDefaults defaults) {
        var profile = ReadIdentityOptional(
            defaults: defaults,
            reader: ref reader
        );
        var hasMappedArrival = reader.ReadBoolean();
        var bodyMotionProgramName = reader.ReadString(
            field: "commit member body motion program",
            maxBytes: MaxStringBytes
        );
        var position = reader.ReadFixedVector();
        var yaw = reader.ReadFixed();
        var planarVelocity = reader.ReadFixedVector();
        var verticalVelocity = reader.ReadFixed();
        var actionContinuity = reader.ReadOptionalClass(
            readValue: static (ref WireReader r) => ReadActionContinuity(reader: ref r)
        );
        var continuum = reader.ReadOptional(
            readValue: static (ref WireReader r) => ReadContinuum(reader: ref r)
        );

        return new WorldTransferCommitMember(
            ActionContinuity: actionContinuity,
            BodyMotionProgramName: bodyMotionProgramName,
            Continuum: continuum,
            HasMappedArrival: hasMappedArrival,
            PlanarVelocity: planarVelocity,
            Position: position,
            Profile: profile,
            VerticalVelocity: verticalVelocity,
            YawRadians: yaw
        );
    }
    private static void WriteReservationMember(WireWriter writer, WorldTransferReservationMember member) {
        WritePrincipal(
            writer: writer,
            principal: member.Principal
        );
        writer.WriteInt32(value: member.PreferredSlot);
        WriteIdentityOptional(
            writer: writer,
            identity: member.Identity
        );
        WriteIntentSource(
            writer: writer,
            source: member.Source
        );
        writer.WriteVector(value: member.BodyColor);
        writer.WriteByte(value: member.CatalogRig);
        writer.WriteOptional(
            value: member.Mobility,
            writeValue: WorldWireLeaves.WriteMobility
        );
    }
    private static WorldTransferReservationMember ReadReservationMember(ref WireReader reader, WorldPlayerDefaults defaults) {
        var principal = WorldWireCodec.ReadPrincipal(reader: ref reader);
        var preferredSlot = reader.ReadInt32();
        var identity = ReadIdentityOptional(
            defaults: defaults,
            reader: ref reader
        );
        var source = WorldWireCodec.ReadIntentSource(reader: ref reader);
        var bodyColor = reader.ReadFiniteVector(field: "reservation member body color");
        var catalogRig = reader.ReadByte();
        var mobility = reader.ReadOptional(
            readValue: static (ref WireReader r) => WorldWireLeaves.ReadMobility(reader: ref r)
        );

        return new WorldTransferReservationMember(
            BodyColor: bodyColor,
            CatalogRig: catalogRig,
            Identity: identity,
            Mobility: mobility,
            PreferredSlot: preferredSlot,
            Principal: principal,
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
    }
    private static WorldTransferReservationRequest ReadReservationRequest(ref WireReader reader, WorldPlayerDefaults defaults) {
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
                defaults: defaults,
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );

        return new WorldTransferReservationRequest(
            Border: border,
            BorderCapacity: borderCapacity,
            DeadlineSourceTick: deadlineSourceTick,
            Members: members,
            PartyAllOrNothing: partyAllOrNothing,
            PeerAdmission: peerAdmission,
            SourceAuthority: sourceAuthority,
            SourceRateHz: sourceRateHz,
            SourceTick: sourceTick,
            TransferId: transferId
        );
    }
    private static void WriteLease(WireWriter writer, WorldTransferLeaseCheckpoint lease) {
        WriteTransferKey(
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
    private static WorldTransferLeaseCheckpoint ReadLease(ref WireReader reader, WorldPlayerDefaults defaults) {
        var key = ReadTransferKey(reader: ref reader);
        var request = ReadReservationRequest(
            defaults: defaults,
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
        WriteTransferKey(
            writer: writer,
            key: committed.Key
        );
        writer.WriteArray(
            items: committed.Members,
            writeItem: WriteCommitMember
        );
        writer.WriteArray(
            items: committed.Principals,
            writeItem: WritePrincipal
        );
        writer.WriteArray(
            items: committed.Incarnations,
            writeItem: WorldWireLeaves.WriteEntityAddress
        );
    }
    private static WorldTransferCommittedCheckpoint ReadCommitted(ref WireReader reader, WorldPlayerDefaults defaults) {
        var key = ReadTransferKey(reader: ref reader);
        var members = reader.ReadArray(
            field: "committed members",
            readItem: (ref WireReader r) => ReadCommitMember(
                defaults: defaults,
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );
        var principals = reader.ReadArray(
            field: "committed principals",
            readItem: static (ref WireReader r) => WorldWireCodec.ReadPrincipal(reader: ref r),
            maximum: MaxCollectionCount
        );
        var incarnations = reader.ReadArray(
            field: "committed incarnations",
            readItem: static (ref WireReader r) => WorldWireLeaves.ReadEntityAddress(reader: ref r),
            maximum: MaxCollectionCount
        );

        return new WorldTransferCommittedCheckpoint(
            Incarnations: incarnations,
            Key: key,
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
                WriteTransferKey(
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
                WriteTransferKey(
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
                WritePrincipal(
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

        return writer.ToArray();
    }
    private static bool TryDecodeEscrow(byte[] bytes, WorldPlayerDefaults defaults, out string reason, out WorldTransferEscrowCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var leases = reader.ReadArray(
            field: "escrow leases",
            readItem: (ref WireReader r) => ReadLease(
                defaults: defaults,
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );
        var committed = reader.ReadArray(
            field: "escrow committed",
            readItem: (ref WireReader r) => ReadCommitted(
                defaults: defaults,
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );
        var latest = reader.ReadArray(
            field: "escrow latest committed transfer",
            readItem: static (ref WireReader r) => {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref r);
                var transfer = ReadTransferKey(reader: ref r);

                return (incarnation, transfer);
            },
            maximum: MaxCollectionCount
        );
        var mobilityLeases = reader.ReadArray(
            field: "escrow mobility leases",
            readItem: static (ref WireReader r) => {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref r);
                var transfer = ReadTransferKey(reader: ref r);
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
                var principal = WorldWireCodec.ReadPrincipal(reader: ref r);

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

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"escrow section: {failure}";

            return false;
        }

        section = new WorldTransferEscrowCheckpoint(
            BorderAdmissions: borderAdmissions,
            Committed: committed,
            LatestCommittedTransfer: latest,
            Leases: leases,
            MobilityAdmissions: mobilityAdmissions,
            MobilityLeases: mobilityLeases
        );
        reason = string.Empty;

        return true;
    }
    // ---- input hold section ----

}
